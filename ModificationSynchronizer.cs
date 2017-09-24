using System;
using System.Threading;
using Open.Disposable;

namespace Open.Threading
{

	public interface IReadOnlyModificationSynchronizer
	{
		void Reading(Action action);

		T Reading<T>(Func<T> action);
	}

	public interface IModificationSynchronizer : IReadOnlyModificationSynchronizer
	{

		bool Modifying(Func<bool> condition, Func<bool> action);
		bool Modifying(Func<bool> action);

		bool Modifying(Action action, bool assumeChange = false);

		bool Modifying<T>(ref T target, T newValue);

		// If this is modifiable, it will increment the version.
		void Poke();

	}


	public sealed class ReadOnlyModificationSynchronizer : IModificationSynchronizer
	{

		public void Reading(Action action)
		{
			action();
		}

		public T Reading<T>(Func<T> action)
		{
			return action();
		}

		public bool Modifying(Action action, bool assumeChange = false)
		{
			throw new NotSupportedException("Synchronizer is read-only.");
		}

		public bool Modifying(Func<bool> action)
		{
			throw new NotSupportedException("Synchronizer is read-only.");
		}

		public bool Modifying(Func<bool> condition, Func<bool> action)
		{
			throw new NotSupportedException("Synchronizer is read-only.");
		}

		public bool Modifying<T>(ref T target, T newValue)
		{
			throw new NotSupportedException("Synchronizer is read-only.");
		}

		public void Poke()
		{
			// Does nothing.
		}

		static ReadOnlyModificationSynchronizer _instance;
		public static ReadOnlyModificationSynchronizer Instance
		{
			get
			{
				return LazyInitializer.EnsureInitialized(ref _instance);
			}
		}
	}


	public class ModificationSynchronizer : DisposableBase, IModificationSynchronizer
	{
		public ModificationSynchronizer()
		{
		}

		public event EventHandler Modified;

		protected int _modifyingDepth = 0;
		protected int _version;

		public int Version
		{
			get { return _version; }
		}

		public void IncrementVersion()
		{
			Interlocked.Increment(ref _version);
		}

		public void Poke()
		{
			Modifying(() => true);
		}


		protected override void OnBeforeDispose()
		{
			Modified = null; // Clean events before swap.
		}

		protected override void OnDispose(bool calledExplicitly)
		{
			Modified = null; // Just in case.
		}


		public virtual void Reading(Action action)
		{
            AssertIsAlive();
			action();
		}

		public virtual T Reading<T>(Func<T> action)
		{
            AssertIsAlive();
			return action();
		}

		protected void SignalModified()
		{
            Modified?.Invoke(this, EventArgs.Empty);
        }

		public bool Modifying(Func<bool> action)
		{
			return Modifying(null, action);
		}

		public bool Modifying(Action action, bool assumeChange = false)
		{
			return Modifying(() =>
			{
				var ver = _version; // Capture the version so that if changes occur indirectly...
				action();
				return assumeChange || ver != _version;
			});
		}

		public virtual bool Modifying(Func<bool> condition, Func<bool> action)
		{
            AssertIsAlive();
			if (condition != null && !condition())
				return false;

			var ver = _version; // Capture the version so that if changes occur indirectly...
			Interlocked.Increment(ref _modifyingDepth);
			var modified = action();
			if (modified) IncrementVersion();
			// At zero depth and version change? Signal.
			if (Interlocked.Decrement(ref _modifyingDepth) == 0 && ver != _version)
				SignalModified();
			return modified;
		}

		public virtual bool Modifying<T>(ref T target, T newValue)
		{
            AssertIsAlive();
			if (target.Equals(newValue)) return false;

			IncrementVersion();
			target = newValue;
			SignalModified();

			return true;
		}
	}

	public sealed class SimpleLockingModificationSynchronizer : ModificationSynchronizer
	{

		readonly object _sync = new Object();

		public SimpleLockingModificationSynchronizer(object sync = null)
		{
			_sync = sync ?? new Object();
		}



		public override void Reading(Action action)
		{
            AssertIsAlive();
			lock (_sync) action();
		}

		public override T Reading<T>(Func<T> action)
		{
			AssertIsAlive();
			lock (_sync) return action();
		}

		public override bool Modifying(Func<bool> condition, Func<bool> action)
		{
			bool modified = false;
			ThreadSafety.LockConditional(
				_sync,
				() => AssertIsAlive() && (condition == null || condition()),
				() => { modified = base.Modifying(null, action); }
			);
			return modified;
		}


		public override bool Modifying<T>(ref T target, T newValue)
		{
			AssertIsAlive();
			if (target.Equals(newValue)) return false;

			lock (_sync) return base.Modifying(ref target, newValue);
		}

	}

	public sealed class ReadWriteModificationSynchronizer : ModificationSynchronizer
	{

		ReaderWriterLockSlim _sync;
		bool _lockOwned;

		public ReadWriteModificationSynchronizer(ReaderWriterLockSlim sync = null)
		{
			if (_sync == null) _lockOwned = true;
			_sync = sync ?? new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
		}

		IDisposable Cleanup()
		{
			return Interlocked.Exchange(ref _sync, null);
		}

		protected override void OnDispose(bool calledExplicitly)
		{
			base.OnDispose(calledExplicitly);
			IDisposable s = null;
			if (!calledExplicitly || !_sync.Write(() => s = Cleanup(), 10 /* Give any cleanup a chance. */ ))
			{
				s = Cleanup();
			}
			if (_lockOwned)
			{
				s?.Dispose();
			}
		}


		public override void Reading(Action action)
		{
			AssertIsAlive();
			_sync.Read(action);
		}

		public override T Reading<T>(Func<T> action)
		{
			AssertIsAlive();
			return _sync.ReadValue(action);
		}

		public override bool Modifying(Func<bool> condition, Func<bool> action)
		{
			AssertIsAlive();

			// Try and early invalidate.
			if (condition != null && !_sync.ReadValue(condition))
				return false;

			bool modified = false;
			_sync.ReadUpgradeable(() =>
			{
				AssertIsAlive();
				if (condition == null || condition())
				{
					modified = _sync.WriteValue(() => base.Modifying(null, action));
				}
			});
			return modified;
		}


		public override bool Modifying<T>(ref T target, T newValue)
		{
			AssertIsAlive();
			if (target.Equals(newValue)) return false;

			bool changed;
			try
			{
				// Note, there's no need for _modifyingDepth recursion tracking here.
				_sync.EnterUpgradeableReadLock();
				AssertIsAlive();

				var ver = _version; // Capture the version so that if changes occur indirectly...
				changed = !target.Equals(newValue);

				if (changed)
				{
					try
					{
						_sync.EnterWriteLock();
						IncrementVersion();
						target = newValue;
					}
					finally
					{
						_sync.ExitWriteLock();
					}


					// Events will be triggered but this thread will still have the upgradable read.
					SignalModified();
				}
			}
			finally
			{
				_sync.ExitUpgradeableReadLock();
			}
			return changed;
		}

	}

}