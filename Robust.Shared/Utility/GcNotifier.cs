using System;

namespace Robust.Shared.Utility
{

    public class GcNotifier
    {

        static GcNotifier()
            // ReSharper disable once ObjectCreationAsStatement
            => new GcNotifier();

        private GcNotifier()
        {
        }

        public static event Action? GarbageCollected;

        ~GcNotifier()
        {
            if (Environment.HasShutdownStarted
                || AppDomain.CurrentDomain.IsFinalizingForUnload())
            {
                return;
            }

            GarbageCollected?.Invoke();

            GC.KeepAlive(new GcNotifier());
        }

    }

}
