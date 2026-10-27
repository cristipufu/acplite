using System.Text;
using Acplite.Client;
using Acplite.Pty;
using Acplite.Sdk;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Acplite.Tests;

public sealed class PtyTests
{
    [Fact]
    public async Task PtyFactory_starts_real_pseudoterminal_and_streams_interactive_io()
    {
        var isWindows = OperatingSystem.IsWindows();
        var options = isWindows
            ? new PtyStartOptions
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = ["/d", "/c", "echo TTY_OK& set /p line=& call echo GOT:%line%"],
                WorkingDirectory = Environment.CurrentDirectory,
                InitialSize = new TerminalSize(100, 30)
            }
            : new PtyStartOptions
            {
                FileName = "/bin/sh",
                Arguments = ["-c", "if [ -t 0 ] && [ -t 1 ]; then echo TTY_OK; fi; read line; echo GOT:$line"],
                WorkingDirectory = Environment.CurrentDirectory,
                InitialSize = new TerminalSize(100, 30)
            };

        await using var pty = PtyFactory.Start(options);

        Assert.True(pty.ProcessId > 0);
        pty.Resize(new TerminalSize(132, 42));

        var inputLine = isWindows ? "hello_from_pty\r\n"u8.ToArray() : "hello_from_pty\n"u8.ToArray();
        await pty.InputStream.WriteAsync(inputLine);
        await pty.InputStream.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sb = new StringBuilder();
        var buf = new byte[1024];

        while (!cts.IsCancellationRequested)
        {
            var n = await pty.OutputStream.ReadAsync(buf, cts.Token);
            if (n <= 0)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (sb.ToString().Contains("GOT:hello_from_pty", StringComparison.Ordinal))
            {
                break;
            }
        }

        var exitCode = await pty.WaitForExitAsync(cts.Token);
        var output = sb.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("TTY_OK", output);
        Assert.Contains("GOT:hello_from_pty", output);
    }

    [Fact]
    public async Task PtyTuiInspector_detects_agy_claude_and_codex_tool_approval_prompts_and_maps_keystrokes()
    {
        var inspector = new PtyTuiInspector();

        // 1. Simulated Antigravity / Claude bordered Ink TUI confirmation prompt with ANSI cursor moves
        var ansiBoxPrompt =
            "\u001b[36m╭──────────────────────────────────────────────────────────╮\u001b[0m\r\n" +
            "\u001b[36m│\u001b[0m \u001b[1mBash(git push origin main)\u001b[0m\u001b[1C\u001b[36m│\u001b[0m\r\n" +
            "\u001b[36m│\u001b[0m Do\u001b[1Cyou\u001b[1Cwant\u001b[1Cto\u001b[1Cproceed?\u001b[36m│\u001b[0m\r\n" +
            "\u001b[36m│\u001b[0m ❯ 1. Yes, allow once\u001b[36m│\u001b[0m\r\n" +
            "\u001b[36m│\u001b[0m   2. Yes, allow always\u001b[36m│\u001b[0m\r\n" +
            "\u001b[36m│\u001b[0m   3. No, suggest changes (esc)\u001b[36m│\u001b[0m\r\n" +
            "\u001b[36m╰──────────────────────────────────────────────────────────╯\u001b[0m\r\n";

        var perm = await inspector.ProcessOutputTextAsync(ansiBoxPrompt);
        Assert.NotNull(perm);
        Assert.Equal("Bash", perm.ToolName);
        Assert.Contains("Bash(git push origin main)", perm.Title);
        Assert.Equal(3, perm.Options.Count);
        Assert.Equal("\r", inspector.ResolveKeystroke(approved: true));
        Assert.Equal("\x1b[B\r", inspector.ResolveKeystroke(approved: true, selectedOptionId: "allow_always"));
        Assert.Equal("\x1b", inspector.ResolveKeystroke(approved: false));

        // Redrawing cursor blink must not emit duplicate permission event
        var duplicate = await inspector.ProcessOutputTextAsync("\u001b[?25h");
        Assert.Null(duplicate);

        // Clearing active permission (e.g. user pressed key in Ghostty) resets state
        var cleared = await inspector.ClearActivePermissionAsync();
        Assert.NotNull(cleared);
        Assert.Null(inspector.ActivePermission);

        // 2. Yes/No style prompt
        var ynPerm = await inspector.ProcessOutputTextAsync("Command: rm -rf ./dist\nAllow execution of this command? [y/n]: ");
        Assert.NotNull(ynPerm);
        Assert.Equal("y\r", inspector.ResolveKeystroke(approved: true));
        Assert.Equal("n\r", inspector.ResolveKeystroke(approved: false));
    }

    [Fact]
    public async Task PtyControlServer_and_AcpliteRuntime_attach_to_live_pty_and_forward_approvals_and_prompts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"acplite-pty-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var prevEnv = Environment.GetEnvironmentVariable("ACPLITE_PTY_DIR");
        Environment.SetEnvironmentVariable("ACPLITE_PTY_DIR", tempDir);

        try
        {
            using var ptyInputBuffer = new MemoryStream();
            var inspector = new PtyTuiInspector();
            var pipeName = $"acplite-test-{Guid.NewGuid():N}"[..28];
            var workspaceDir = Path.Combine(tempDir, "workspace");
            Directory.CreateDirectory(workspaceDir);

            var metadata = new PtySessionMetadata
            {
                Pid = Environment.ProcessId,
                ChildPid = Environment.ProcessId,
                Provider = "agy",
                PipeName = pipeName,
                WorkingDirectory = workspaceDir,
                SessionId = "conv-ghostty-123",
                TranscriptPath = null,
                StartedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow
            };

            await using var server = new PtyControlServer(metadata, ptyInputBuffer, inspector, tempDir);
            await server.StartAsync();

            // Simulate a tool approval dialog appearing in Ghostty BEFORE the mobile client attaches
            var detected = await inspector.ProcessOutputTextAsync(
                "Bash(dotnet test -c Release)\nDo you want to proceed?\n1. Yes, allow once\n2. Always allow\n3. No");
            Assert.NotNull(detected);

            await using var runtime = new AcpliteRuntime(externalSessionBridge: PtySessionBridge.Instance);
            await using var handle = await runtime.StartSessionAsync(new AcpSessionConfig
            {
                Provider = AgentProviderKind.Antigravity,
                WorkingDirectory = workspaceDir,
                SessionId = "conv-ghostty-123",
                ResumeExisting = true
            });

            Assert.IsType<PtyAttachedSessionHandle>(handle);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var enumerator = handle.ReadEventsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

            // Immediately receives the pre-existing Ghostty permission prompt upon attach!
            Assert.True(await enumerator.MoveNextAsync());
            var permEvent = Assert.IsType<AcpPermissionRequestEvent>(enumerator.Current);
            Assert.Equal("Bash", permEvent.ToolName);
            Assert.Contains("dotnet test -c Release", permEvent.Title);

            // Approve from mobile -> writes '\r' to Ghostty PTY input and emits resolution event
            await handle.RespondPermissionAsync(permEvent.RequestId, approved: true, cancellationToken: cts.Token);

            Assert.True(await enumerator.MoveNextAsync());
            var resolvedEvent = Assert.IsType<AcpPermissionRequestEvent>(enumerator.Current);
            Assert.Empty(resolvedEvent.Options);

            // Send a prompt from mobile -> writes prompt + '\r' to Ghostty PTY input
            await handle.SendPromptAsync("ship the release", cts.Token);

            var writtenBytes = Encoding.UTF8.GetString(ptyInputBuffer.ToArray());
            Assert.Equal("\rship the release\r", writtenBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACPLITE_PTY_DIR", prevEnv);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ShellHookInstaller_installs_and_uninstalls_idempotently_across_shells()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"acplite-hooks-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var zshrc = Path.Combine(tempHome, ".zshrc");
            await File.WriteAllTextAsync(zshrc, "export PATH=\"$HOME/.local/bin:$PATH\"\n");

            var status1 = await ShellHookInstaller.InstallAsync("zsh", tempHome, "tunnelite acp-pty");
            Assert.True(status1.IsInstalled);

            // Re-installing is idempotent
            var status2 = await ShellHookInstaller.InstallAsync("zsh", tempHome, "tunnelite acp-pty");
            Assert.True(status2.IsInstalled);

            var content = await File.ReadAllTextAsync(zshrc);
            Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", content);
            Assert.Contains("tunnelite acp-pty", content);
            Assert.Equal(
                content.IndexOf(ShellHookInstaller.StartMarker, StringComparison.Ordinal),
                content.LastIndexOf(ShellHookInstaller.StartMarker, StringComparison.Ordinal));

            // Install bash, fish, pwsh, and powershell as well
            Assert.True((await ShellHookInstaller.InstallAsync("bash", tempHome)).IsInstalled);
            Assert.True((await ShellHookInstaller.InstallAsync("fish", tempHome)).IsInstalled);
            Assert.True((await ShellHookInstaller.InstallAsync("pwsh", tempHome)).IsInstalled);
            Assert.True((await ShellHookInstaller.InstallAsync("powershell", tempHome)).IsInstalled);

            var allStatuses = await ShellHookInstaller.GetAllStatusesAsync(tempHome);
            Assert.Equal(5, allStatuses.Count);
            Assert.All(allStatuses, static s => Assert.True(s.IsInstalled));

            var removed = await ShellHookInstaller.UninstallAsync("zsh", tempHome);
            Assert.False(removed.IsInstalled);

            var afterRemove = await File.ReadAllTextAsync(zshrc);
            Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"", afterRemove);
            Assert.DoesNotContain(ShellHookInstaller.StartMarker, afterRemove);

            var customAgents = ShellHookInstaller.ParseProvidersList("claude,codex,opencode,hermes,pi");
            var customSnippet = ShellHookInstaller.BuildHookSnippet("zsh", "acplite pty", customAgents);
            Assert.Contains("opencode() { _acplite_pty_wrap opencode \"$@\"; }", customSnippet);
            Assert.Contains("hermes() { _acplite_pty_wrap hermes \"$@\"; }", customSnippet);
            Assert.Contains("pi() { _acplite_pty_wrap pi \"$@\"; }", customSnippet);
            Assert.NotNull(PtyFactory.ResolveExecutableOnPath("dotnet"));
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }
}
