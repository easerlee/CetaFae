using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CetaFae.Probe;

/// <summary>Reads only protection metadata from Windows' legacy accessibility bridge.</summary>
internal sealed class GuideLegacyPrivacy(IntPtr window, CancellationToken token)
{
    Dictionary<string, object?>? _states;

    internal object? ReadState(AutomationElement element)
    {
        _states ??= ReadStates();
        token.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != window) throw new InvalidDataException("target_changed");
        return _states.GetValueOrDefault(Key(element.GetRuntimeId()));
    }

    Dictionary<string, object?> ReadStates()
    {
        var pid = Native.ProcessId(window);
        void Validate()
        {
            token.ThrowIfCancellationRequested();
            if (pid == 0 || !Native.IsWindow(window) || Native.ProcessId(window) != pid || Native.GetForegroundWindow() != window)
            {
                ProbeLog.Log("guide_legacy_binding", ("phase", "foreground"), ("alive", Native.IsWindow(window)),
                    ("sameProcess", Native.ProcessId(window) == pid), ("foreground", Native.GetForegroundWindow() == window));
                throw new InvalidDataException("target_changed");
            }
        }
        Validate();
        var owned = new List<object>();
        T Own<T>(T value) where T : class { owned.Add(value); return value; }
        try
        {
            var automation = Own((IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!)!);
            var root = Own(automation.ElementFromHandle(window));
            if (!root.GetRuntimeId().SequenceEqual(AutomationElement.FromHandle(window).GetRuntimeId()))
            {
                ProbeLog.Log("guide_legacy_binding", ("phase", "runtime_id"), ("sameRoot", false));
                throw new InvalidDataException("target_changed");
            }
            var walker = Own(automation.RawViewWalker());
            var result = new Dictionary<string, object?>();
            var count = 0;
            void Visit(IElement node, int depth)
            {
                Validate();
                if (++count > 4096 || depth > 48) throw new InvalidDataException("privacy_unavailable");
                // Never descend into hidden content or request its legacy state.
                if (Property(node, 30022) is true) return;
                var key = Key(node.GetRuntimeId());
                var state = Property(node, 30090) is true ? Property(node, 30096) : null;
                if (!result.TryAdd(key, state)) throw new InvalidDataException("privacy_unavailable");
                for (var child = walker.FirstChild(node); child is not null;)
                {
                    Own(child);
                    Visit(child, depth + 1);
                    child = walker.NextSibling(child);
                }
            }
            Visit(root, 0);
            Validate();
            return result;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("privacy_unavailable"); }
        finally
        {
            // These wrappers belong solely to this bounded metadata pass, never the managed UIA client.
            foreach (var value in owned.AsEnumerable().Reverse()) Marshal.ReleaseComObject(value);
        }
    }

    static string Key(int[] id) => id.Length is > 0 and <= 64 ? string.Join(",", id)
        : throw new InvalidDataException("privacy_unavailable");

    static object? Property(IElement element, int property)
    {
        var value = element.Property(property, true);
        // Explicitly unsupported values are COM sentinels, not false or a zero state.
        if (value is bool or int) return value;
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        return null;
    }

    // Vtable order follows Microsoft's UIAutomationClient.h. Unused slots are never invoked.
    // No name/value, capture, input, desktop-root or focus operation is exposed by this adapter.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IElement ElementFromHandle(IntPtr handle);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void ControlViewWalker(); void ContentViewWalker();
        IWalker RawViewWalker();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache();
        void BuildUpdatedCache(); void GetCurrentPropertyValue();
        [return: MarshalAs(UnmanagedType.Struct)] object? Property(int id, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWalker
    {
        void GetParentElement();
        IElement? FirstChild(IElement element);
        void GetLastChildElement();
        IElement? NextSibling(IElement element);
    }
}
