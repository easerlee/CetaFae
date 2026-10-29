using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CetaFae.Probe;

/// <summary>Conservative local refusal for recognizable credentials, independent of application plugins.</summary>
internal static class GuidePrivacy
{
    static readonly Regex CredentialLabel = new(
        @"^\s*(?:api[ _-]?key|access[ _-]?token|refresh[ _-]?token|client[ _-]?secret|password|passwd|API密钥|密钥|访问令牌|令牌|密码)\s*[:：]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    static readonly Regex Credential = new(
        "-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----|" +
        @"(?:gh[pousr]_[A-Za-z0-9]{20,255}|github_pat_[A-Za-z0-9_]{20,255}|sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,255}|AKIA[A-Z0-9]{16})|" +
        @"(?:api[ _-]?key|access[ _-]?token|refresh[ _-]?token|client[ _-]?secret|password|passwd|密钥|令牌|密码)\s*[=:：]\s*[""']?[^\s""';,，；]{4,}|" +
        @"authorization\s*:\s*(?:bearer|basic)\s+\S+|[?&](?:token|secret)=[^&#\s]{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void EnsureTextSafe(string text)
    {
        if (text.Length > 32 * 1024) throw new InvalidDataException("privacy_unavailable");
        if (Credential.IsMatch(text)) throw new InvalidDataException("sensitive_content");
    }

    internal static string? ReadVisibleName(object? isOffscreen, object? isPassword, Func<string> readName,
        string controlType = "", bool inTitleBar = false)
    {
        if (isOffscreen is true) return null;
        // Password metadata must be checked before invoking even the name getter.
        if (isPassword is true) throw new InvalidDataException("sensitive_content");
        // UIA's explicit NotSupported sentinel has documented defaults; provider failures do not.
        if (controlType == "ControlType.Document" && isPassword is false &&
            ReferenceEquals(isOffscreen, AutomationElement.NotSupported)) isOffscreen = false;
        var structuralDefaults = controlType is "ControlType.Window" or "ControlType.Pane";
        var captionDefaults = inTitleBar &&
            (controlType is "ControlType.TitleBar" or "ControlType.MenuBar" or "ControlType.MenuItem" or "ControlType.Button");
        if (structuralDefaults || captionDefaults)
        {
            if (ReferenceEquals(isOffscreen, AutomationElement.NotSupported)) isOffscreen = false;
            if (ReferenceEquals(isPassword, AutomationElement.NotSupported)) isPassword = false;
        }
        // Standard content still needs explicit visibility; editable children are checked independently.
        var standardPasswordDefault = controlType is "ControlType.Button" or "ControlType.Tab" or "ControlType.TabItem" or
            "ControlType.Text" or "ControlType.ComboBox" or "ControlType.Tree" or "ControlType.TreeItem" or
            "ControlType.Group" or "ControlType.RadioButton" or "ControlType.CheckBox";
        if (isOffscreen is false && standardPasswordDefault && ReferenceEquals(isPassword, AutomationElement.NotSupported))
            isPassword = false;
        if (isOffscreen is not false || isPassword is not false) throw new InvalidDataException("privacy_unavailable");
        var name = readName();
        EnsureTextSafe(name);
        // An explicit credential field label is sufficient to refuse its pixels without reading a value pattern.
        if (CredentialLabel.IsMatch(name)) throw new InvalidDataException("sensitive_content");
        return name;
    }
}
