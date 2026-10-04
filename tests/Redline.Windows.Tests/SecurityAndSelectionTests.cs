using Redline.Windows;
using Redline.Windows.Automation;
using Xunit;

namespace Redline.Windows.Tests;

public class SecurityFilterTests
{
    private static ElementInfo Info(
        string process = "notepad.exe", bool isPassword = false, string automationId = "", string name = "",
        bool enabled = true, bool value = true, bool readOnly = false, int pid = 999_999, string className = "") => new()
    {
        RuntimeId = "1.2.3",
        ClassName = className,
        ProcessId = pid,
        ProcessName = process,
        ControlType = "Edit",
        AutomationId = automationId,
        Name = name,
        IsPassword = isPassword,
        IsEnabled = enabled,
        SupportsTextPattern = true,
        SupportsValuePattern = value,
        ValueIsReadOnly = readOnly,
    };

    private readonly SecurityFilter _filter = new();

    [Fact]
    public void OrdinaryEditor_IsAllowed() => Assert.True(_filter.Evaluate(Info()).Allowed);

    [Theory]
    [InlineData(true, "", "", "notepad.exe")]           // IsPassword
    [InlineData(false, "txtPassword", "", "notepad.exe")]
    [InlineData(false, "otp_input", "", "notepad.exe")]
    [InlineData(false, "", "Password", "notepad.exe")]
    [InlineData(false, "", "Verification code", "notepad.exe")]
    [InlineData(false, "", "", "KeePass.exe")]
    [InlineData(false, "", "", "1PASSWORD.EXE")]         // case-insensitive
    public void SensitiveSurfaces_AreBlocked(bool isPassword, string automationId, string name, string process)
    {
        Assert.False(_filter.Evaluate(Info(process, isPassword, automationId, name)).Allowed);
    }

    [Fact]
    public void ProseThatMentionsPasswords_IsNotBlocked()
    {
        // Name heuristics only match labels that *start* with a credential word.
        Assert.True(_filter.Evaluate(Info(name: "Message body — don't share your password")).Allowed);
    }

    [Fact]
    public void ReadOnlyAndDisabled_AreBlocked()
    {
        Assert.False(_filter.Evaluate(Info(readOnly: true)).Allowed);
        Assert.False(_filter.Evaluate(Info(enabled: false)).Allowed);
    }

    [Fact]
    public void CredentialBlocks_AreSensitive_ReadOnlyAndDisabledAreNot()
    {
        // Sensitive blocks must detach immediately; the others may use the focus-bounce grace period.
        Assert.True(_filter.Evaluate(Info(isPassword: true)).Sensitive);
        Assert.True(_filter.Evaluate(Info(automationId: "txtPassword")).Sensitive);
        Assert.True(_filter.Evaluate(Info("KeePass.exe")).Sensitive);
        Assert.False(_filter.Evaluate(Info(readOnly: true)).Sensitive);
        Assert.False(_filter.Evaluate(Info(enabled: false)).Sensitive);
    }

    [Theory]
    [InlineData("xterm-helper-textarea")] // VS Code / Antigravity integrated terminal
    [InlineData("TermControl")]           // Windows Terminal
    [InlineData("ConsoleWindowClass")]    // conhost
    public void TerminalInput_IsBlockedAsSensitive(string className)
    {
        var decision = _filter.Evaluate(Info("Code.exe", className: className));
        Assert.False(decision.Allowed);
        Assert.True(decision.Sensitive);
    }

    [Fact]
    public void UserExclusions_AddToBuiltIns_AndCanBeReplaced()
    {
        var filter = new SecurityFilter();
        filter.SetUserExclusions(["Vault.exe"]);
        Assert.False(filter.Evaluate(Info("vault.exe")).Allowed);
        Assert.False(filter.Evaluate(Info("KeePass.exe")).Allowed); // built-in stays

        filter.SetUserExclusions([]);
        Assert.True(filter.Evaluate(Info("Vault.exe")).Allowed);
        Assert.False(filter.Evaluate(Info("KeePass.exe")).Allowed); // still built-in
    }

    [Fact]
    public void OwnProcess_IsBlocked()
    {
        Assert.False(_filter.Evaluate(Info(pid: Environment.ProcessId)).Allowed);
    }

    [Fact]
    public void CustomExclusionList_ReplacesDefaults()
    {
        var filter = new SecurityFilter(["notepad.exe"]);
        Assert.False(filter.Evaluate(Info("notepad.exe")).Allowed);
        Assert.True(filter.Evaluate(Info("KeePass.exe")).Allowed);
    }
}

public class AdapterSelectorTests
{
    private static ElementInfo Info(string controlType, string framework, bool text, bool value, bool readOnly = false, string className = "", bool focusable = true) => new()
    {
        RuntimeId = "1",
        ClassName = className,
        IsKeyboardFocusable = focusable,
        ControlType = controlType,
        FrameworkId = framework,
        SupportsTextPattern = text,
        SupportsValuePattern = value,
        ValueIsReadOnly = readOnly,
    };

    private readonly AdapterSelector _selector = new([new GenericUiaAdapterFactory()]);

    [Theory]
    [InlineData("Edit", "Win32", true, true)]       // classic edit
    [InlineData("Document", "Win32", true, true)]   // Notepad (RichEditD2DPT)
    [InlineData("Document", "Win32", true, false)]  // Word (_WwG)
    [InlineData("Edit", "Chrome", true, true)]      // TinyMCE / CKEditor
    [InlineData("Edit", "WPF", false, true)]        // ValuePattern-only
    public void EditableSurfaces_AreHandled(string controlType, string framework, bool text, bool value)
    {
        Assert.NotNull(_selector.Select(Info(controlType, framework, text, value)));
    }

    [Theory]
    [InlineData("Document", "Chrome", true, false, false)] // web page body
    [InlineData("Document", "Chrome", true, true, true)]   // read-only web document
    [InlineData("Button", "Win32", false, false, false)]
    [InlineData("Edit", "Win32", false, false, false)]     // no text access at all
    public void NonEditableSurfaces_AreRejected(string controlType, string framework, bool text, bool value, bool readOnly)
    {
        Assert.Null(_selector.Select(Info(controlType, framework, text, value, readOnly)));
    }

    [Fact]
    public void ChromiumContentEditableGroup_IsHandled_ButFocusableCardIsNot()
    {
        Assert.NotNull(_selector.Select(Info("Group", "Chrome", text: true, value: false)));               // contenteditable div
        Assert.Null(_selector.Select(Info("Group", "Chrome", text: false, value: false)));                 // div tabindex=0
        Assert.Null(_selector.Select(Info("Group", "Chrome", text: true, value: false, focusable: false))); // not focusable
        Assert.Null(_selector.Select(Info("Group", "Win32", text: true, value: false)));                   // only trusted for Chromium
    }

    [Theory]
    [InlineData("Win32", "NetUITextbox")]          // Word ribbon font name / size
    [InlineData("Win32", "NetUISearchBoxTextbox")] // Office "Search" box
    [InlineData("Chrome", "OmniboxViewViews")]     // Chrome / Edge address bar
    public void NonProseCommandControls_AreRejected(string framework, string className)
    {
        Assert.Null(_selector.Select(Info("Edit", framework, true, true, className: className)));
    }

    private sealed class HighPriorityFactory : ITextSurfaceAdapterFactory
    {
        public string Name => "Special";
        public int Priority => 10;
        public bool CanHandle(ElementInfo info) => info.FrameworkId == "Chrome";
        public Redline.Core.Interfaces.ITextSurfaceAdapter Create(UiaDispatcher uia, System.Windows.Automation.AutomationElement element, ElementInfo info) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void HigherPriorityFactory_Wins()
    {
        var selector = new AdapterSelector([new GenericUiaAdapterFactory(), new HighPriorityFactory()]);
        Assert.Equal("Special", selector.Select(Info("Edit", "Chrome", true, true))!.Name);
        Assert.Equal("GenericUia", selector.Select(Info("Edit", "Win32", true, true))!.Name);
    }
}
