﻿using Open.Disposable;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace Open.Threading
{
	[SuppressMessage("ReSharper", "MemberCanBeProtected.Global")]
	[SuppressMessage("ReSharper", "VirtualMemberNeverOverridden.Global")]
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	public class AsyncProcess<T> : DisposableBase
		where T : new()
	{

		public AsyncProcess(Action<T> closure, TaskScheduler scheduler = null)
		{
			Closure = closure;

			Scheduler = scheduler ?? TaskScheduler.Default;
			Count = 0;
		}

		protected readonly ReaderWriterLockSlim SyncLock = new ReaderWriterLockSlim();

		protected TaskScheduler Scheduler
		{
			get;
			private set;
		}

		protected Action<T> Closure
		{
			get;
			private set;
		}

		protected Task InternalTask
		{
			get;
			set;
		}

		public virtual DateTime LatestCompleted
		{
			get;
			protected set;
		}

		public Exception LastFault
		{
			get;
			protected set;
		}

		public int Count
		{
			get;
			protected set;
		}

		public bool HasBeenRun => Count != 0;

		//long _processCount = 0;
		protected virtual void Process(object progress)
		{
			if (progress == null)
				throw new ArgumentNullException(nameof(progress));
			Contract.EndContractBlock();

			var p = (T)progress;
			try
			{
				//Contract.Assert(Interlocked.Increment(ref _processCount) == 1);
				Closure(p);
				SyncLock.Write(() => LatestCompleted = DateTime.Now);
			}
			catch (Exception ex)
			{
				SyncLock.Write(() => LastFault = ex);
			}
			//finally
			//{
			//	//Interlocked.Decrement(ref _processCount);
			//}
		}

		protected virtual Task EnsureProcess(bool once, TimeSpan? timeAllowedBeforeRefresh = null)
		{
			Task task = null;
			SyncLock.ReadWriteConditionalOptimized(
				write =>
				{
					task = InternalTask;
					return (task == null || !once && !task.IsActive()) // No action, or completed?
						&& (!timeAllowedBeforeRefresh.HasValue // Now?
							|| timeAllowedBeforeRefresh.Value < DateTime.Now - LatestCompleted); // Or later?
				}, () =>
				{

					task = new Task(Process, new T());
					task.Start(Scheduler);
					InternalTask = task;
					Count++;

				}
			);

			return task;
		}

		// ReSharper disable once MemberCanBeProtected.Global
		public bool IsRunning => InternalTask?.IsActive() ?? false;

		public void Wait(bool once = true)
		{
			EnsureActive(once);
			InternalTask?.Wait();
		}

		public bool EnsureActive(bool once = false)
			=> EnsureProcess(once).IsActive();

		// ReSharper disable once MemberCanBeProtected.Global
		public virtual T Progress
		{
			get
			{
				var t = InternalTask;
				if (t == null)
					return new T();

				var state = t.AsyncState;
				var result = (T)state;
				return result;
			}
		}



		protected override void OnDispose()
		{
			SyncLock.Dispose();
			Scheduler = null;
			InternalTask = null;
			Closure = null;
		}

	}

	public class AsyncProcess : AsyncProcess<Progress>
	{
		// ReSharper disable once MemberCanBeProtected.Global
		public AsyncProcess(Action<Progress> closure, TaskScheduler scheduler = null)
			: base(closure, scheduler)
		{
		}

		public string TimeStatistics
		{
			get
			{
				var result = string.Empty;
				if (LatestCompleted != default)
					result += "\n" + (DateTime.Now - LatestCompleted).ToString() + " ago";

				if (!IsRunning) return result;
				var p = Progress;
				result += "\n" + p.EstimatedTimeLeftString + " remaining";

				return result;
			}
		}

		protected override void Process(object progress)
		{
			var p = (Progress)progress;
			try
			{
				//Contract.Assert(Interlocked.Increment(ref _processCount) == 1);
				Closure(p);
			}
			catch (Exception ex)
			{
				SyncLock.Write(() => LatestCompleted = DateTime.Now);
				p.Failed(ex.ToString());
			}
			//finally
			//{
			//	//Interlocked.Decrement(ref _processCount);
			//}
		}

	}
}
