using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Acplite.Client;
using Acplite.Sdk;
using Acplite.Sdk.Adapters;
using Acplite.Sdk.Bridge;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;
using Acplite.Sdk.Transport;

namespace Acplite.Tests;

public sealed class AdapterAndBridgeTests
{
    [Fact]
    public async Task ClaudeCodeAdapter_discovers_and_tails_jsonl_sessions_and_streams_turns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acplite-claude-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectDir = Path.Combine(tempDir, "-Users-me-repo");
            Directory.CreateDirectory(projectDir);
            var sessionPath = Path.Combine(projectDir, "sess-claude-123.jsonl");

            await File.WriteAllLinesAsync(sessionPath,
            [
                "not-json-at-all-should-be-skipped-gracefully",
                "{\"type\":\"user\",\"sessionId\":\"sess-claude-123\",\"cwd\":\"/Users/me/repo\",\"gitBranch\":\"main\",\"timestamp\":\"2026-10-26T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Fix the race condition in TcpClientStore\"}]}}",
                "{\"type\":\"assistant\",\"sessionId\":\"sess-claude-123\",\"timestamp\":\"2026-10-26T10:00:05Z\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Checking SemaphoreSlim gates...\"},{\"type\":\"text\",\"text\":\"I updated TcpClientStore to release the gate cleanly.\"}]}}"
            ]);

            var adapter = new ClaudeCodeAdapter(tempDir);
            var discovered = await adapter.DiscoverSessionsAsync();

            Assert.Single(discovered);
            var sess = discovered[0];
            Assert.Equal("sess-claude-123", sess.SessionId);
            Assert.Equal("Fix the race condition in TcpClientStore", sess.Title);
            Assert.Equal("/Users/me/repo", sess.WorkingDirectory);
            Assert.Equal("main", sess.GitBranch);
            Assert.Equal("claude-sonnet-4-5", sess.Model);

            var historyEvents = new List<AcpEvent>();
            await foreach (var ev in adapter.TailSessionTranscriptAsync(sess, includeHistory: true, followLive: false))
            {
                historyEvents.Add(ev);
            }

            Assert.Equal(2, historyEvents.Count);
            var assistantEntry = Assert.IsType<AcpTranscriptHistoryEntryEvent>(historyEvents[1]);
            Assert.Equal("assistant", assistantEntry.Role);
            Assert.Contains("SemaphoreSlim", assistantEntry.Thinking);
            Assert.Contains("TcpClientStore", assistantEntry.Text);

            using var agentToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var hostReadFromAgent = new AnonymousPipeClientStream(PipeDirection.In, agentToHostPipe.ClientSafePipeHandle);
            using var hostToAgentPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var agentReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToAgentPipe.ClientSafePipeHandle);

            await using var handle = await adapter.StartSessionFromStreamsAsync(
                new AcpSessionConfig
                {
                    Provider = AgentProviderKind.Claude,
                    WorkingDirectory = Path.GetTempPath(),
                    SessionId = "sess-claude-123",
                    AutoApproveTools = false
                },
                hostReadFromAgent,
                hostToAgentPipe);

            using var agentReader = new StreamReader(agentReadFromHost, Encoding.UTF8);
            await using var agentWriter = new StreamWriter(agentToHostPipe, new UTF8Encoding(false)) { AutoFlush = true };

            var initLine = await agentReader.ReadLineAsync();
            Assert.NotNull(initLine);
            Assert.Contains("\"subtype\":\"initialize\"", initLine);

            await handle.SendPromptAsync("Run unit tests");
            var promptLine = await agentReader.ReadLineAsync();
            Assert.NotNull(promptLine);
            Assert.Contains("Run unit tests", promptLine);

            var steered = await handle.SteerAsync("Focus on TcpClientStore");
            Assert.True(steered);
            var steerLine = await agentReader.ReadLineAsync();
            Assert.NotNull(steerLine);
            Assert.Contains("Focus on TcpClientStore", steerLine);

            await handle.SetModelAsync("claude-opus-4-6");
            var modelLine = await agentReader.ReadLineAsync();
            Assert.NotNull(modelLine);
            Assert.Contains("\"subtype\":\"set_model\"", modelLine);

            await handle.CancelTurnAsync();
            var cancelLine = await agentReader.ReadLineAsync();
            Assert.NotNull(cancelLine);
            Assert.Contains("\"subtype\":\"interrupt\"", cancelLine);

            await agentWriter.WriteLineAsync("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-claude-123\",\"model\":\"claude-sonnet-4-5\",\"cwd\":\"/Users/me/repo\"}");
            await agentWriter.WriteLineAsync("{\"type\":\"stream_event\",\"session_id\":\"sess-claude-123\",\"event\":{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\"}}}");
            await agentWriter.WriteLineAsync("{\"type\":\"stream_event\",\"session_id\":\"sess-claude-123\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Planning test run...\"}}}");
            await agentWriter.WriteLineAsync("{\"type\":\"stream_event\",\"session_id\":\"sess-claude-123\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Running dotnet test...\"}}}");
            await agentWriter.WriteLineAsync("{\"type\":\"control_request\",\"request_id\":\"perm_1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Bash\",\"input\":{\"command\":\"dotnet test\"}}}");

            var observedEvents = new List<AcpEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var ev in handle.ReadEventsAsync(cts.Token))
            {
                observedEvents.Add(ev);
                if (ev is AcpPermissionRequestEvent perm)
                {
                    await handle.RespondPermissionAsync(perm.RequestId, approved: true, cancellationToken: cts.Token);
                    var permRespLine = await agentReader.ReadLineAsync(cts.Token);
                    Assert.NotNull(permRespLine);
                    Assert.Contains("\"behavior\":\"allow\"", permRespLine);

                    await agentWriter.WriteLineAsync("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":420,\"total_cost_usd\":0.015,\"usage\":{\"input_tokens\":120,\"output_tokens\":45}}");
                }
                else if (ev is AcpTurnCompletedEvent)
                {
                    break;
                }
            }

            Assert.Contains(observedEvents, e => e is AcpSessionStartedEvent);
            Assert.Contains(observedEvents, e => e is AcpToolCallEvent tc && tc.ToolName == "Bash" && tc.Kind == "execute");
            Assert.Contains(observedEvents, e => e is AcpThinkingDeltaEvent t && t.Delta == "Planning test run...");
            Assert.Contains(observedEvents, e => e is AcpAssistantTextDeltaEvent a && a.Delta == "Running dotnet test...");
            Assert.Contains(observedEvents, e => e is AcpPermissionRequestEvent p && p.ToolName == "Bash");
            var completed = Assert.Single(observedEvents.OfType<AcpTurnCompletedEvent>());
            Assert.Equal("end_turn", completed.StopReason);
            Assert.Equal(120, completed.InputTokens);
            Assert.Equal(45, completed.OutputTokens);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CodexAdapter_discovers_sessions_and_handles_threads_steering_approvals_and_rate_limits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acplite-codex-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dayDir = Path.Combine(tempDir, "2026", "10", "26");
            Directory.CreateDirectory(dayDir);
            var rolloutPath = Path.Combine(dayDir, "rollout-2026-10-26T12-00-00-019a1111-2222-7333-8444-555555555555.jsonl");

            await File.WriteAllLinesAsync(rolloutPath,
            [
                "{\"timestamp\":\"2026-10-26T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"019a1111-2222-7333-8444-555555555555\",\"cwd\":\"/Users/me/codex-app\",\"git\":{\"branch\":\"feat/acp\"}}}",
                "{\"timestamp\":\"2026-10-26T12:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.3-codex\"}}",
                "{\"timestamp\":\"2026-10-26T12:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Optimize the buffer pool\"}}",
                "{\"timestamp\":\"2026-10-26T12:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_reasoning\",\"text\":\"Checking ArrayPool usage...\"}}",
                "{\"timestamp\":\"2026-10-26T12:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"Rented 8KB chunks from ArrayPool.\"}}"
            ]);

            var adapter = new CodexAdapter(tempDir);
            var sessions = await adapter.DiscoverSessionsAsync();

            Assert.Single(sessions);
            Assert.Equal("019a1111-2222-7333-8444-555555555555", sessions[0].SessionId);
            Assert.Equal("Optimize the buffer pool", sessions[0].Title);
            Assert.Equal("gpt-5.3-codex", sessions[0].Model);
            Assert.Equal("feat/acp", sessions[0].GitBranch);

            var rateLimits = CodexTranscriptParser.ParseRateLimitsResponse(JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("rateLimits");
                w.WriteStartObject();
                w.WriteString("planType", "plus");
                w.WritePropertyName("primary");
                w.WriteStartObject();
                w.WriteNumber("usedPercent", 42.5);
                w.WriteNumber("resetsAt", 1793000000);
                w.WriteEndObject();
                w.WritePropertyName("secondary");
                w.WriteStartObject();
                w.WriteNumber("usedPercent", 18.0);
                w.WriteNumber("resetsAt", 1793500000);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }));
            Assert.Equal("plus", rateLimits.PlanType);
            Assert.Equal(42.5, rateLimits.FiveHourPercentUsed);
            Assert.Equal(18.0, rateLimits.SevenDayPercentUsed);

            using var agentToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var hostReadFromAgent = new AnonymousPipeClientStream(PipeDirection.In, agentToHostPipe.ClientSafePipeHandle);
            using var hostToAgentPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var agentReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToAgentPipe.ClientSafePipeHandle);

            var serverTransport = new AcpJsonRpcTransport(agentReadFromHost, agentToHostPipe);
            string? steeredText = null;
            var interrupted = false;

            serverTransport.OnRequestAsync = (id, method, param, ct) =>
            {
                return Task.FromResult<JsonElement?>(method switch
                {
                    "initialize" => JsonElementBuilder.Build(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("userAgent", "codex-test");
                        w.WriteEndObject();
                    }),
                    "thread/resume" => JsonElementBuilder.Build(w =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("thread");
                        w.WriteStartObject();
                        w.WriteString("id", "thread_codex_99");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }),
                    "turn/start" => JsonElementBuilder.Build(w =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("turn");
                        w.WriteStartObject();
                        w.WriteString("id", "turn_1");
                        w.WriteString("status", "inProgress");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }),
                    "turn/steer" => JsonElementBuilder.Build(w =>
                    {
                        steeredText = param?.GetProperty("input")[0].GetProperty("text").GetString();
                        w.WriteStartObject();
                        w.WriteEndObject();
                    }),
                    "turn/interrupt" => JsonElementBuilder.Build(w =>
                    {
                        interrupted = true;
                        w.WriteStartObject();
                        w.WriteEndObject();
                    }),
                    _ => JsonElementBuilder.EmptyObject
                });
            };
            serverTransport.Start();

            await using var handle = await adapter.StartSessionFromStreamsAsync(
                new AcpSessionConfig
                {
                    Provider = AgentProviderKind.Codex,
                    WorkingDirectory = Path.GetTempPath(),
                    SessionId = "thread_codex_99",
                    ResumeExisting = true,
                    AutoApproveTools = false
                },
                hostReadFromAgent,
                hostToAgentPipe);

            Assert.Equal("thread_codex_99", handle.SessionId);

            await handle.SendPromptAsync("Build the feature");
            var steered = await handle.SteerAsync("Also add XML docs");
            Assert.True(steered);
            Assert.Equal("Also add XML docs", steeredText);

            await handle.CancelTurnAsync();
            Assert.True(interrupted);

            await serverTransport.SendNotificationAsync("item/started", JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("item");
                w.WriteStartObject();
                w.WriteString("id", "cmd_1");
                w.WriteString("type", "commandExecution");
                w.WriteString("command", "dotnet build");
                w.WriteEndObject();
                w.WriteEndObject();
            }));

            var approvalTask = serverTransport.SendRequestAsync("item/commandExecution/requestApproval", JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("command", "dotnet build");
                w.WriteEndObject();
            }));

            var events = new List<AcpEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var ev in handle.ReadEventsAsync(cts.Token))
            {
                events.Add(ev);
                if (ev is AcpPermissionRequestEvent perm)
                {
                    await handle.RespondPermissionAsync(perm.RequestId, approved: true, cancellationToken: cts.Token);
                    var decisionJson = await approvalTask.WaitAsync(cts.Token);
                    Assert.Equal("accept", decisionJson.GetProperty("decision").GetString());

                    await serverTransport.SendNotificationAsync("item/agentMessage/delta", JsonElementBuilder.Build(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("itemId", "msg_1");
                        w.WriteString("delta", "Feature built with XML docs.");
                        w.WriteEndObject();
                    }), cts.Token);

                    await serverTransport.SendNotificationAsync("turn/completed", JsonElementBuilder.Build(w =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("turn");
                        w.WriteStartObject();
                        w.WriteString("id", "turn_1");
                        w.WriteString("status", "completed");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }), cts.Token);
                }
                else if (ev is AcpTurnCompletedEvent)
                {
                    break;
                }
            }

            Assert.Contains(events, e => e is AcpToolCallEvent tc && tc.Title == "dotnet build");
            Assert.Contains(events, e => e is AcpPermissionRequestEvent);
            Assert.Contains(events, e => e is AcpAssistantTextDeltaEvent d && d.Delta == "Feature built with XML docs.");
            await serverTransport.DisposeAsync();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GrokAdapter_and_NativeAcpSessionHandle_support_sessions_billing_interject_and_permissions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acplite-grok-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sessionFile = Path.Combine(tempDir, "grok-sess-42.jsonl");
            await File.WriteAllLinesAsync(sessionFile,
            [
                "{\"role\":\"user\",\"text\":\"Explain xAI interject\"}",
                "{\"role\":\"assistant\",\"text\":\"_x.ai/interject steers active turns.\"}"
            ]);

            var adapter = new GrokAdapter(tempDir);
            var discovered = await adapter.DiscoverSessionsAsync();
            Assert.Single(discovered);
            Assert.Equal("grok-sess-42", discovered[0].SessionId);
            Assert.Equal("Explain xAI interject", discovered[0].Title);

            var billing = GrokAdapter.ParseGrokBillingResponse(JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("planType", "Grok Pro");
                w.WriteNumber("creditsRemainingUsd", 19.75);
                w.WriteEndObject();
            }));
            Assert.Equal("Grok Pro", billing.PlanType);
            Assert.Equal(19.75m, billing.CreditsRemainingUsd);

            using var agentToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var hostReadFromAgent = new AnonymousPipeClientStream(PipeDirection.In, agentToHostPipe.ClientSafePipeHandle);
            using var hostToAgentPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var agentReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToAgentPipe.ClientSafePipeHandle);

            var serverTransport = new AcpJsonRpcTransport(agentReadFromHost, agentToHostPipe);
            string? interjectedText = null;
            string? updatedModel = null;
            var cancelled = false;

            serverTransport.OnNotificationAsync = (method, _, _) =>
            {
                if (method == "session/cancel")
                {
                    cancelled = true;
                }
                return Task.CompletedTask;
            };

            serverTransport.OnRequestAsync = async (id, method, param, ct) =>
            {
                switch (method)
                {
                    case "initialize":
                        return JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WriteNumber("protocolVersion", 1);
                            w.WriteEndObject();
                        });

                    case "session/new":
                        return JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WriteString("sessionId", "grok-sess-42");
                            w.WriteEndObject();
                        });

                    case "_x.ai/interject":
                        interjectedText = param?.GetOptionalString("text");
                        return JsonElementBuilder.EmptyObject;

                    case "session/set_model":
                        updatedModel = param?.GetOptionalString("modelId");
                        return JsonElementBuilder.EmptyObject;

                    case "session/prompt":
                    {
                        await serverTransport.SendNotificationAsync("_x.ai/session/update", JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WritePropertyName("update");
                            w.WriteStartObject();
                            w.WriteString("sessionUpdate", "agent_thought_chunk");
                            w.WritePropertyName("content");
                            w.WriteStartObject();
                            w.WriteString("text", "Reasoning via Grok...");
                            w.WriteEndObject();
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }), ct);

                        var permRes = await serverTransport.SendRequestAsync("session/request_permission", JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WritePropertyName("toolCall");
                            w.WriteStartObject();
                            w.WriteString("title", "Write file");
                            w.WriteEndObject();
                            w.WritePropertyName("options");
                            w.WriteStartArray();
                            w.WriteStartObject();
                            w.WriteString("optionId", "allow_once_id");
                            w.WriteString("name", "Allow Once");
                            w.WriteString("kind", "allow_once");
                            w.WriteEndObject();
                            w.WriteEndArray();
                            w.WriteEndObject();
                        }), ct);

                        var selectedOpt = permRes.GetProperty("outcome").GetProperty("optionId").GetString();
                        await serverTransport.SendNotificationAsync("session/update", JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WritePropertyName("update");
                            w.WriteStartObject();
                            w.WriteString("sessionUpdate", "agent_message_chunk");
                            w.WritePropertyName("content");
                            w.WriteStartObject();
                            w.WriteString("text", $"Permission chosen: {selectedOpt}");
                            w.WriteEndObject();
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }), ct);

                        return JsonElementBuilder.Build(w =>
                        {
                            w.WriteStartObject();
                            w.WriteString("stopReason", "end_turn");
                            w.WriteEndObject();
                        });
                    }

                    default:
                        return JsonElementBuilder.EmptyObject;
                }
            };
            serverTransport.Start();

            await using var handle = await adapter.StartSessionFromStreamsAsync(
                new AcpSessionConfig
                {
                    Provider = AgentProviderKind.Grok,
                    WorkingDirectory = Path.GetTempPath(),
                    AutoApproveTools = false
                },
                hostReadFromAgent,
                hostToAgentPipe);

            Assert.Equal("grok-sess-42", handle.SessionId);

            await handle.SendPromptAsync("Test Grok ACP");
            Assert.True(await handle.SteerAsync("Steer via xAI interject"));
            Assert.Equal("Steer via xAI interject", interjectedText);

            await handle.SetModelAsync("grok-4-heavy");
            Assert.Equal("grok-4-heavy", updatedModel);

            await handle.CancelTurnAsync();

            var collected = new List<AcpEvent>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var ev in handle.ReadEventsAsync(cts.Token))
            {
                collected.Add(ev);
                if (ev is AcpPermissionRequestEvent perm)
                {
                    await handle.RespondPermissionAsync(perm.RequestId, approved: true, cancellationToken: cts.Token);
                }
                else if (ev is AcpTurnCompletedEvent)
                {
                    break;
                }
            }

            Assert.True(cancelled);
            Assert.Contains(collected, e => e is AcpThinkingDeltaEvent t && t.Delta == "Reasoning via Grok...");
            Assert.Contains(collected, e => e is AcpAssistantTextDeltaEvent a && a.Delta == "Permission chosen: allow_once_id");
            await serverTransport.DisposeAsync();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AntigravityAdapter_discovers_and_live_tails_brain_transcript_and_streams_interactive_frames()
    {
        var tempBrain = Path.Combine(Path.GetTempPath(), "acplite-agy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempBrain);
        try
        {
            var convId = "f7962729-9a31-488d-88f0-32469176bef4";
            var logsDir = Path.Combine(tempBrain, convId, ".system_generated", "logs");
            Directory.CreateDirectory(logsDir);
            var transcriptPath = Path.Combine(logsDir, "transcript.jsonl");

            await File.WriteAllLinesAsync(transcriptPath,
            [
                "{\"step_index\":0,\"type\":\"USER_INPUT\",\"status\":\"DONE\",\"created_at\":\"2026-10-26T11:00:00Z\",\"content\":\"Command Working Directory: /Users/me/tunnelite\\n<USER_REQUEST>\\nBuild the ACP mobile bridge\\n</USER_REQUEST>\"}",
                "{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"2026-10-26T11:00:04Z\",\"thinking\":\"Designing Acplite.Sdk...\",\"content\":\"Here is the implementation plan.\",\"tool_calls\":[{\"id\":\"tc_1\",\"name\":\"write_file\"}]}"
            ]);

            var adapter = new AntigravityAdapter(tempBrain);
            var sessions = await adapter.DiscoverSessionsAsync();

            Assert.Single(sessions);
            Assert.Equal(convId, sessions[0].SessionId);
            Assert.Equal("Build the ACP mobile bridge", sessions[0].Title);
            Assert.Equal("/Users/me/tunnelite", sessions[0].WorkingDirectory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = new List<AcpEvent>();

            var tailTask = Task.Run(async () =>
            {
                await foreach (var ev in adapter.TailSessionTranscriptAsync(sessions[0], includeHistory: true, followLive: true, cts.Token))
                {
                    received.Add(ev);
                    if (received.Count >= 4)
                    {
                        break;
                    }
                }
            }, cts.Token);

            await Task.Delay(150, cts.Token);
            await File.AppendAllTextAsync(
                transcriptPath,
                "{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"2026-10-26T11:01:00Z\",\"content\":\"Live delta from terminal session!\"}\n",
                cts.Token);

            await tailTask;

            Assert.Contains(received, e => e is AcpTranscriptHistoryEntryEvent u && u.Role == "user" && u.Text == "Build the ACP mobile bridge");
            Assert.Contains(received, e => e is AcpToolCallEvent tc && tc.ToolName == "write_file");
            Assert.Contains(received, e => e is AcpTranscriptHistoryEntryEvent a && a.Text == "Live delta from terminal session!");

            using var agentToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var hostReadFromAgent = new AnonymousPipeClientStream(PipeDirection.In, agentToHostPipe.ClientSafePipeHandle);
            using var hostToAgentPipe = new AnonymousPipeServerStream(PipeDirection.Out);
            using var agentReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToAgentPipe.ClientSafePipeHandle);

            await using var handle = await adapter.StartSessionFromStreamsAsync(
                new AcpSessionConfig
                {
                    Provider = AgentProviderKind.Antigravity,
                    WorkingDirectory = Path.GetTempPath(),
                    SessionId = convId
                },
                hostReadFromAgent,
                hostToAgentPipe);

            await using var agentWriter = new StreamWriter(agentToHostPipe, new UTF8Encoding(false)) { AutoFlush = true };
            await handle.SendPromptAsync("Continue Step 2");
            await agentWriter.WriteLineAsync("{\"type\":\"PLANNER_RESPONSE\",\"thinking\":\"Checking Program.cs...\",\"content\":\"Integrating tunnelite acp.\"}");
            await agentWriter.WriteLineAsync("{\"type\":\"result\",\"is_error\":false}");

            var liveEvents = new List<AcpEvent>();
            await foreach (var ev in handle.ReadEventsAsync(cts.Token))
            {
                liveEvents.Add(ev);
                if (ev is AcpTurnCompletedEvent)
                {
                    break;
                }
            }

            Assert.Contains(liveEvents, e => e is AcpThinkingDeltaEvent t && t.Delta == "Checking Program.cs...");
            Assert.Contains(liveEvents, e => e is AcpAssistantTextDeltaEvent a && a.Delta == "Integrating tunnelite acp.");
        }
        finally
        {
            Directory.Delete(tempBrain, recursive: true);
        }
    }

    [Fact]
    public async Task AcpStdioServerBridge_bridges_provider_adapter_over_standard_acp_jsonrpc()
    {
        var fakeAdapter = new FakeInMemoryProviderAdapter();

        using var hostToBridgePipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var bridgeReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToBridgePipe.ClientSafePipeHandle);
        using var bridgeToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostReadFromBridge = new AnonymousPipeClientStream(PipeDirection.In, bridgeToHostPipe.ClientSafePipeHandle);

        await using var bridge = new AcpStdioServerBridge(fakeAdapter, bridgeReadFromHost, bridgeToHostPipe);
        bridge.StartBackground();

        var hostUpdates = new List<string>();
        await using var hostClient = new AcpJsonRpcTransport(hostReadFromBridge, hostToBridgePipe)
        {
            OnNotificationAsync = (method, param, _) =>
            {
                if (method == "session/update" && param.HasValue &&
                    param.Value.TryGetObject("update", out var u) &&
                    u.TryGetObject("content", out var c))
                {
                    var text = c.GetOptionalString("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        hostUpdates.Add(text);
                    }
                }
                return Task.CompletedTask;
            }
        };
        hostClient.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var initRes = await hostClient.SendRequestAsync("initialize", JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("protocolVersion", 1);
            w.WriteEndObject();
        }), cts.Token);
        Assert.Equal(1, initRes.GetProperty("protocolVersion").GetInt32());

        var newRes = await hostClient.SendRequestAsync("session/new", JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("cwd", Path.GetTempPath());
            w.WriteEndObject();
        }), cts.Token);
        var sessionId = newRes.GetProperty("sessionId").GetString()!;
        Assert.Equal("fake-sess-1", sessionId);

        var promptRes = await hostClient.SendRequestAsync("session/prompt", JsonElementBuilder.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", sessionId);
            w.WritePropertyName("prompt");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", "Hello ACP Bridge");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }), cts.Token);

        Assert.Equal("end_turn", promptRes.GetProperty("stopReason").GetString());
        Assert.Contains("Bridged response to: Hello ACP Bridge", hostUpdates);
    }

    [Fact]
    public async Task AcpJsonRpcTransport_handles_concurrent_requests_remote_errors_and_handler_exceptions()
    {
        using var clientToServerPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverReadPipe = new AnonymousPipeClientStream(PipeDirection.In, clientToServerPipe.ClientSafePipeHandle);
        using var serverToClientPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientReadPipe = new AnonymousPipeClientStream(PipeDirection.In, serverToClientPipe.ClientSafePipeHandle);

        await using var server = new AcpJsonRpcTransport(serverReadPipe, serverToClientPipe)
        {
            OnRequestAsync = async (_, method, param, ct) =>
            {
                if (method == "boom")
                {
                    throw new InvalidOperationException("Simulated server failure");
                }

                await Task.Delay(Random.Shared.Next(5, 25), ct);
                var n = param?.GetProperty("n").GetInt32() ?? 0;
                return JsonElementBuilder.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("doubled", n * 2);
                    w.WriteEndObject();
                });
            }
        };
        server.Start();

        await using var client = new AcpJsonRpcTransport(clientReadPipe, clientToServerPipe);
        client.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(1, 16).Select(async i =>
        {
            var res = await client.SendRequestAsync("double", JsonElementBuilder.Build(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("n", i);
                w.WriteEndObject();
            }), cts.Token);
            Assert.Equal(i * 2, res.GetProperty("doubled").GetInt32());
        });
        await Task.WhenAll(tasks);

        var ex = await Assert.ThrowsAsync<JsonRpcRemoteException>(() =>
            client.SendRequestAsync("boom", null, cts.Token));
        Assert.Equal(-32603, ex.Code);
        Assert.Contains("Simulated server failure", ex.Message);
    }

    [Fact]
    public async Task Child_process_stderr_flood_draining_and_unexpected_crash_reporting_work_without_deadlock()
    {
        var dotnetPath = ProcessTreeStopper.ResolveBinaryPath("dotnet");
        Assert.NotNull(dotnetPath);

        var version = await ProcessTreeStopper.ProbeVersionAsync(dotnetPath!);
        Assert.False(string.IsNullOrWhiteSpace(version));

        // Spawn dotnet with an invalid command so it writes diagnostics to stderr and exits non-zero
        var proc = ProcessTreeStopper.StartAgentProcess(
            dotnetPath!,
            ["invalid-command-that-does-not-exist-acplite-test"],
            Path.GetTempPath());

        await using var handle = new AntigravitySessionHandle(
            new AcpSessionConfig
            {
                Provider = AgentProviderKind.Antigravity,
                WorkingDirectory = Path.GetTempPath()
            },
            "crash-test-session",
            proc.StandardOutput.BaseStream,
            proc.StandardInput.BaseStream,
            proc);

        await handle.InitializeAsync();

        var events = new List<AcpEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in handle.ReadEventsAsync(cts.Token))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e =>
            e is AcpInterruptionEvent intr &&
            intr.Classification.Kind == AcpInterruptionKind.ProcessCrashed);
        Assert.Contains(events, e =>
            e is AcpTurnCompletedEvent done &&
            done.StopReason == "error");
    }

    [Fact]
    public async Task Cli_and_TerminalText_strip_ansi_injections_and_execute_commands()
    {
        var malicious = "Hello \x1b[31mRED\x1b[0m and \x1b]0;malicious-window-title\x07world\x01\x02!";
        var cleaned = TerminalText.Clean(malicious);
        Assert.Equal("Hello RED and world!", cleaned);

        var exitHelp = await Program.Main(["--help"]);
        Assert.Equal(0, exitHelp);

        var exitProviders = await Program.Main(["providers", "--json"]);
        Assert.Equal(0, exitProviders);
    }

    [Fact]
    public async Task AcpliteRuntime_unregisters_disposed_sessions_and_enforces_handshake_timeout()
    {
        var fakeAdapter = new FakeInMemoryProviderAdapter();
        await using var runtime = new AcpliteRuntime([fakeAdapter]);

        Assert.Equal(0, runtime.ActiveSessionCount);

        var session = await runtime.StartSessionAsync(new AcpSessionConfig
        {
            Provider = AgentProviderKind.Antigravity,
            WorkingDirectory = Path.GetTempPath()
        });

        Assert.Equal(1, runtime.ActiveSessionCount);

        await session.DisposeAsync();
        Assert.Equal(0, runtime.ActiveSessionCount);

        using var unresponsiveOutPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostReadPipe = new AnonymousPipeClientStream(PipeDirection.In, unresponsiveOutPipe.ClientSafePipeHandle);
        using var hostWritePipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var unresponsiveInPipe = new AnonymousPipeClientStream(PipeDirection.In, hostWritePipe.ClientSafePipeHandle);

        var codexAdapter = new CodexAdapter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await codexAdapter.StartSessionFromStreamsAsync(
                new AcpSessionConfig
                {
                    Provider = AgentProviderKind.Codex,
                    WorkingDirectory = Path.GetTempPath(),
                    HandshakeTimeout = TimeSpan.FromMilliseconds(150)
                },
                hostReadPipe,
                hostWritePipe);
        });
    }

    [Fact]
    public async Task Concurrent_DisposeAsync_and_Bridge_EOF_shutdown_are_idempotent_and_safe()
    {
        var fakeAdapter = new FakeInMemoryProviderAdapter();
        var runtime = new AcpliteRuntime([fakeAdapter]);
        var session = await runtime.StartSessionAsync(new AcpSessionConfig
        {
            Provider = AgentProviderKind.Antigravity,
            WorkingDirectory = Path.GetTempPath()
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.DisposeAsync().AsTask()));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.DisposeAsync().AsTask()));

        using var hostToBridgePipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var bridgeReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToBridgePipe.ClientSafePipeHandle);
        using var bridgeToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostReadFromBridge = new AnonymousPipeClientStream(PipeDirection.In, bridgeToHostPipe.ClientSafePipeHandle);

        var bridge = new AcpStdioServerBridge(fakeAdapter, bridgeReadFromHost, bridgeToHostPipe);
        var runTask = bridge.RunAsync();

        hostToBridgePipe.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => bridge.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task SessionHandle_queues_prompts_while_busy_auto_drains_on_turn_completion_and_supports_interrupt_and_prompt()
    {
        var adapter = new AntigravityAdapter();

        using var agentToHostPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostReadFromAgent = new AnonymousPipeClientStream(PipeDirection.In, agentToHostPipe.ClientSafePipeHandle);
        using var hostToAgentPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var agentReadFromHost = new AnonymousPipeClientStream(PipeDirection.In, hostToAgentPipe.ClientSafePipeHandle);

        await using var handle = await adapter.StartSessionFromStreamsAsync(
            new AcpSessionConfig
            {
                Provider = AgentProviderKind.Antigravity,
                WorkingDirectory = Path.GetTempPath(),
                SessionId = "agy-queue-test"
            },
            hostReadFromAgent,
            hostToAgentPipe);

        using var agentReader = new StreamReader(agentReadFromHost, Encoding.UTF8);
        await using var agentWriter = new StreamWriter(agentToHostPipe, new UTF8Encoding(false)) { AutoFlush = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.False(handle.IsTurnActive);

        // 1. Enqueue while idle dispatches immediately and returns null
        var firstQueued = await handle.EnqueuePromptAsync("Turn 1 prompt", cts.Token);
        Assert.Null(firstQueued);
        Assert.True(handle.IsTurnActive);

        var line1 = await agentReader.ReadLineAsync(cts.Token);
        Assert.NotNull(line1);
        Assert.Contains("Turn 1 prompt", line1);

        // 2. Enqueue while busy appends to FIFO queue and returns item metadata
        var q1 = await handle.EnqueuePromptAsync("Turn 2 prompt (will remove)", cts.Token);
        var q2 = await handle.EnqueuePromptAsync("Turn 2 prompt (kept)", cts.Token);
        var q3 = await handle.EnqueuePromptAsync("Turn 3 prompt", cts.Token);

        Assert.NotNull(q1);
        Assert.NotNull(q2);
        Assert.NotNull(q3);
        Assert.Equal(3, handle.GetQueuedPrompts().Count);

        // 3. Remove q1 before Turn 1 finishes
        var removed = await handle.RemoveQueuedPromptAsync(q1!.QueueItemId, cts.Token);
        Assert.True(removed);
        Assert.Equal(2, handle.GetQueuedPrompts().Count);
        Assert.Equal("Turn 2 prompt (kept)", handle.GetQueuedPrompts()[0].Prompt);

        // 4. Complete Turn 1 -> q2 should auto-drain immediately
        await agentWriter.WriteLineAsync("{\"type\":\"result\",\"is_error\":false}");

        var line2 = await agentReader.ReadLineAsync(cts.Token);
        Assert.NotNull(line2);
        Assert.Contains("Turn 2 prompt (kept)", line2);
        Assert.Single(handle.GetQueuedPrompts());
        Assert.True(handle.IsTurnActive);

        // 5. While Turn 2 is active, call InterruptAndSendPromptAsync -> sends interrupt control frame + new prompt immediately, keeping q3 for after!
        var interruptTask = handle.InterruptAndSendPromptAsync("Urgent interrupt prompt", cts.Token);

        var cancelLine = await agentReader.ReadLineAsync(cts.Token);
        Assert.NotNull(cancelLine);
        Assert.Contains("\"subtype\":\"interrupt\"", cancelLine);

        // Agent acknowledges interrupt with result frame
        await agentWriter.WriteLineAsync("{\"type\":\"result\",\"is_error\":false}");
        await interruptTask;

        var urgentLine = await agentReader.ReadLineAsync(cts.Token);
        Assert.NotNull(urgentLine);
        Assert.Contains("Urgent interrupt prompt", urgentLine);
        Assert.Single(handle.GetQueuedPrompts());

        // 6. Complete Urgent turn -> q3 auto-drains!
        await agentWriter.WriteLineAsync("{\"type\":\"result\",\"is_error\":false}");

        var line3 = await agentReader.ReadLineAsync(cts.Token);
        Assert.NotNull(line3);
        Assert.Contains("Turn 3 prompt", line3);
        Assert.Empty(handle.GetQueuedPrompts());

        // 7. Complete Turn 3 -> session returns to idle
        await agentWriter.WriteLineAsync("{\"type\":\"result\",\"is_error\":false}");

        var observedQueueEvents = 0;
        var observedCompletedTurns = 0;
        await foreach (var ev in handle.ReadEventsAsync(cts.Token))
        {
            if (ev is AcpPromptQueueUpdatedEvent)
            {
                observedQueueEvents++;
            }
            else if (ev is AcpTurnCompletedEvent)
            {
                observedCompletedTurns++;
                if (observedCompletedTurns >= 4)
                {
                    break;
                }
            }
        }

        Assert.True(observedQueueEvents >= 5);
        for (var i = 0; i < 20 && handle.IsTurnActive; i++)
        {
            await Task.Delay(10, cts.Token);
        }
        Assert.False(handle.IsTurnActive);
    }

    [Fact]
    public async Task Codex_and_Antigravity_parsers_and_TranscriptTailer_handle_response_items_tool_summaries_and_tail_windows()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "acplite-tail-window-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dayDir = Path.Combine(tempDir, "2026", "10", "26");
            Directory.CreateDirectory(dayDir);
            var rolloutPath = Path.Combine(dayDir, "rollout-2026-10-26T18-00-00-01a14b83-8e97-7d21-a424-fd6b4ecc0313.jsonl");

            await File.WriteAllLinesAsync(rolloutPath,
            [
                "{\"timestamp\":\"2026-10-26T18:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"01a14b83-8e97-7d21-a424-fd6b4ecc0313\",\"cwd\":\"/Users/me/petrina\",\"git\":{\"branch\":\"main\"}}}",
                "{\"timestamp\":\"2026-10-26T18:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"# AGENTS.md instructions for /Users/me/petrina\"}]}}",
                "{\"timestamp\":\"2026-10-26T18:00:01Z\",\"type\":\"world_state\",\"payload\":{\"full\":true}}",
                "{\"timestamp\":\"2026-10-26T18:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_settings\":{\"model\":\"gpt-6.1-sol\"}}}",
                "{\"timestamp\":\"2026-10-26T18:00:03Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Revamp the acceptance test suite\"}]}}",
                "{\"timestamp\":\"2026-10-26T18:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Revamp the acceptance test suite\"}}",
                "{\"timestamp\":\"2026-10-26T18:00:04Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Inspecting test layers and runner fixtures...\"}]}}",
                "{\"timestamp\":\"2026-10-26T18:00:05Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"call_1\",\"name\":\"exec\",\"input\":\"text(await tools.exec_command({cmd:\\\"dotnet test -c Release\\\"}))\"}}",
                "{\"timestamp\":\"2026-10-26T18:00:06Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"agent_message\",\"id\":\"amsg_1\",\"author\":\"/root/test_layers_batch\",\"recipient\":\"/root\",\"content\":[{\"type\":\"input_text\",\"text\":\"Message Type: FINAL_ANSWER\\nTask name: /root\\nPayload:\\nPreserves all 24 variants.\"}]}}",
                "{\"timestamp\":\"2026-10-26T18:00:07Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\",\"content\":[{\"type\":\"output_text\",\"text\":\"The shared-product fixture batch passed all 71 tests.\"}]}}"
            ]);

            var codexAdapter = new CodexAdapter(tempDir);
            var sessions = await codexAdapter.DiscoverSessionsAsync();
            Assert.Single(sessions);
            Assert.Equal("01a14b83-8e97-7d21-a424-fd6b4ecc0313", sessions[0].SessionId);
            Assert.Equal("Revamp the acceptance test suite", sessions[0].Title);
            Assert.Equal("/Users/me/petrina", sessions[0].WorkingDirectory);
            Assert.Equal("gpt-6.1-sol", sessions[0].Model);

            var events = new List<AcpEvent>();
            await foreach (var ev in codexAdapter.TailSessionTranscriptAsync(sessions[0], includeHistory: true, followLive: false))
            {
                events.Add(ev);
            }

            // Injected AGENTS.md user message skipped; duplicate user_message deduplicated
            Assert.Single(events.OfType<AcpTranscriptHistoryEntryEvent>(), e => e.Role == "user");
            Assert.Contains(events, e => e is AcpTranscriptHistoryEntryEvent h && h.Thinking == "Inspecting test layers and runner fixtures...");
            Assert.Contains(events, e => e is AcpToolCallEvent tc && tc.ToolName == "exec" && tc.Title == "dotnet test -c Release");
            Assert.Contains(events, e => e is AcpToolCallEvent tc && tc.ToolName == "subagent" && tc.Title.Contains("test_layers_batch", StringComparison.Ordinal));
            Assert.Contains(events, e => e is AcpTranscriptHistoryEntryEvent h && h.Role == "assistant" && h.Text == "The shared-product fixture batch passed all 71 tests.");

            // Verify Antigravity tool call argument summary extraction without duplicate ToolName on history entry
            var agyEvents = AntigravityTranscriptParser.ParseTranscriptLine(
                "conv-1",
                "{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"thinking\":\"Reading source...\",\"tool_calls\":[{\"id\":\"tc_9\",\"name\":\"read_file\",\"args\":{\"Path\":\"/Users/me/repo/src/Program.cs\",\"StartLine\":10,\"EndLine\":50}}]}").ToList();
            Assert.Equal(2, agyEvents.Count);
            var agyHist = Assert.IsType<AcpTranscriptHistoryEntryEvent>(agyEvents[0]);
            Assert.Equal("Reading source...", agyHist.Thinking);
            Assert.Null(agyHist.ToolName);
            var agyTool = Assert.IsType<AcpToolCallEvent>(agyEvents[1]);
            Assert.Equal("read_file", agyTool.ToolName);
            Assert.Equal("src/Program.cs:10-50", agyTool.Title);

            // Verify TranscriptTailer tail-window seeking skips old prefix and aligns to newline boundary
            var largeFile = Path.Combine(tempDir, "large-tail.jsonl");
            var sb = new StringBuilder();
            for (var i = 0; i < 200; i++)
            {
                sb.AppendLine($"{{\"idx\":{i},\"pad\":\"{new string('x', 80)}\"}}");
            }
            await File.WriteAllTextAsync(largeFile, sb.ToString());

            var tailedLines = new List<string>();
            await foreach (var line in TranscriptTailer.TailLinesAsync(
                largeFile,
                includeExistingLines: true,
                followLiveChanges: false,
                maxInitialBytes: 1024))
            {
                tailedLines.Add(line);
            }

            Assert.NotEmpty(tailedLines);
            Assert.True(tailedLines.Count < 20);
            Assert.StartsWith("{\"idx\":", tailedLines[0], StringComparison.Ordinal);
            Assert.Contains("\"idx\":199", tailedLines[^1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class FakeInMemoryProviderAdapter : IAcpProviderAdapter
    {
        public AgentProviderKind Provider => AgentProviderKind.Antigravity;
        public string ShortId => "agy";
        public string DisplayName => "Fake Antigravity";
        public string DefaultBinaryName => "agy";
        public bool IsNativeAcp => false;
        public bool SupportsLiveSteering => true;
        public bool SupportsSessionDiscovery => false;
        public bool SupportsUsageProbe => false;

        public Task<AcpProviderDescriptor> DiscoverInstalledAsync(string? binaryPathOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AcpProviderDescriptor(Provider, ShortId, DisplayName, DefaultBinaryName, true, "/bin/agy", "1.0.0", false, true, false, false));

        public Task<IReadOnlyList<AcpDiscoveredSession>> DiscoverSessionsAsync(int maxResults = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AcpDiscoveredSession>>([]);

        public async IAsyncEnumerable<AcpEvent> TailSessionTranscriptAsync(AcpDiscoveredSession session, bool includeHistory = true, bool followLive = true, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async Task<IAcpSessionHandle> StartSessionAsync(AcpSessionConfig config, CancellationToken cancellationToken = default)
        {
            var handle = new FakeSessionHandle(config);
            await handle.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return handle;
        }

        public Task<AcpUsageSnapshot> ProbeUsageAsync(string? binaryPathOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AcpUsageSnapshot(Provider, DateTimeOffset.UtcNow));

        private sealed class FakeSessionHandle : AcpSessionHandleBase
        {
            public FakeSessionHandle(AcpSessionConfig config)
                : base(AgentProviderKind.Antigravity, config, Stream.Null, Stream.Null, process: null)
            {
                SessionId = "fake-sess-1";
            }

            public override Task InitializeAsync(CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            protected override Task SendPromptCoreAsync(string prompt, CancellationToken cancellationToken = default)
            {
                var now = DateTimeOffset.UtcNow;
                PublishEvent(new AcpAssistantTextDeltaEvent(Provider, SessionId, now, $"Bridged response to: {prompt}"));
                PublishEvent(new AcpTurnCompletedEvent(Provider, SessionId, now, "end_turn"));
                return Task.CompletedTask;
            }

            public override Task<bool> SteerAsync(string steeringPrompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public override Task RespondPermissionAsync(string requestId, bool approved, string? selectedOptionId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public override Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public override Task CancelTurnAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
