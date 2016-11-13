using System;
using System.Threading;
using System.Threading.Tasks;

namespace NamedPipeWrapper
{
	public static class TaskHelpers
	{
		public static void MuteExceptions(this Task task)
		{
			if (task.Exception != null)
				task.Exception.Handle(x => true);
		}

		public static void HandleExceptionLight(this Task task, Action<Exception> handler)
		{
			if (task.Exception != null)
			{
				task.Exception.Handle(exception =>
				{
					handler(exception);
					return true;
				});
			}
		}

		public static void HandleException(this Task task, Action<Exception> handler)
		{
		    if (SynchronizationContext.Current == null)
		    {
		        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
		    }

			task.ContinueWith(result =>
			{
				if (result.Exception != null)
				{
					result.Exception.Handle(exception =>
					{
						handler(exception);
						return true;
					});
				}
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}
	}
}