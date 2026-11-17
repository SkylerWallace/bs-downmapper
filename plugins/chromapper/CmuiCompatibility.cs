using System.Reflection;

namespace BsDownmap.ChroMapper
{
    internal static class CmuiCompatibility
    {
        private static class Callbacks<T>
        {
            internal static readonly MethodInfo SetSilently = typeof(CMUIComponent<T>)
                .GetMethod("SetValueWithoutNotify", new[] { typeof(T) });
            internal static readonly FieldInfo Changed = typeof(CMUIComponent<T>)
                .GetField("onValueChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        internal static void SetValueWithoutNotify<T>(CMUIComponent<T> control, T value)
        {
            // Resolve the optional API from the running editor.
            var setSilently = Callbacks<T>.SetSilently;
            if (setSilently != null)
            {
                setSilently.Invoke(control, new object[] { value });
                return;
            }

            // Stable's Value setter updates the UI and invokes the registered callback.
            var field = Callbacks<T>.Changed
                ?? throw new MissingFieldException(typeof(CMUIComponent<T>).FullName, "onValueChanged");
            var callback = field.GetValue(control);
            field.SetValue(control, null);
            try { control.Value = value; }
            finally { field.SetValue(control, callback); }
        }
    }
}
