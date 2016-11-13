using System;
using System.Threading;
using System.Threading.Tasks;

namespace NamedPipeWrapper
{
    internal static class TaskExtensions
	{
	    public static void ThrowExceptions(this Task task)
	    {
	        task.HandleException(ex =>
	        {
	            throw ex;
	        });
	    }

	    public static void MuteExceptions(this Task task)
	    {
	        task.HandleException(x => {});
	    }

		public static void MuteExceptionsLight(this Task task)
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