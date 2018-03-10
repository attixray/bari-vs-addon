using System;

namespace KOTEM.BariVSPackage.BariExtension.Utils
{
    public class SimpleStateHolder : IDisposable
    {
        private bool isOnState;
        private readonly Action cleanUpAction;

        public bool IsOnState => isOnState;

        public SimpleStateHolder(bool initialState = true, Action cleanUpAction = null)
        {
            isOnState = initialState;
            this.cleanUpAction = cleanUpAction;
        }
        public SimpleStateHolder(Action setUpAction, Action cleanUpAction)
        {
            setUpAction();
            this.cleanUpAction = cleanUpAction;
        }

        public void Dispose()
        {
            isOnState = false;
            cleanUpAction?.Invoke();
        }
    }
}
