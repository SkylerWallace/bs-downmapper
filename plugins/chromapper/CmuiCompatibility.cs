using System.Reflection;

namespace BsDownmap.ChroMapper
{
    internal static class CmuiCompatibility
    {
        private static class Callbacks<T>
        {
            internal static readonly FieldInfo Changed = typeof(CMUIComponent<T>)
                .GetField("onValueChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        // Stable's Value setter updates the UI and invokes the registered callback.
        internal static void SetValueWithoutNotify<T>(this CMUIComponent<T> control, T value)
        {
            var field = Callbacks<T>.Changed
                ?? throw new MissingFieldException(typeof(CMUIComponent<T>).FullName, "onValueChanged");
            var callback = field.GetValue(control);
            field.SetValue(control, null);
            try { control.Value = value; }
            finally { field.SetValue(control, callback); }
        }
    }
}
