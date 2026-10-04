using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Llm;
using Xunit;

namespace LTFI.Infrastructure.Tests;

/// <summary>Records requests and replies with canned text. Never touches the network.</summary>
internal sealed class MockLlmProvider(string reply, bool configured = true) : ILlmProvider
{
    public List<LlmRequest> Requests { get; } = [];

    public bool IsConfigured => configured;

    public string Model => "mock-model";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmResponse(reply, Model, new LlmUsage(120, 80)));
    }
}

/// <summary>Fake HTTP transport: returns queued responses and captures what was sent.</summary>
internal sealed class FakeHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
{
    private int _next;
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        var (status, body) = replies[Math.Min(_next++, replies.Length - 1)];
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

internal sealed class FixedKeyStore(string? key) : IApiKeyStore
{
    public string? GetKey() => key;
    public string? KeySource => key is null ? null : "test";
    public void SetKey(string k) { }
    public void ClearKey() { }
}

public class CoachTests
{
    private const string ValidCard = """
        {
          "headline": "Solid week on LTFI, quiet on Thesis.",
          "wins": [{ "text": "Shipped the review page", "evidence": "4 tasks completed" }],
          "patterns": [{ "observation": "Focus happened in mornings", "evidence": "3 of 4 sessions before noon", "effect": "helped" }],
          "risks": [{ "risk": "Thesis keeps slipping", "if_then": "If I open email first, then I will close it and open the outline." }],
          "last_week_review": [],
          "commitments": [{
            "title": "Draft thesis intro", "why": "Stalled 15 days", "project_title": "Thesis",
            "first_step": "Open the outline and write one sentence", "first_step_minutes": 25,
            "when_where": "Mon 9:30, desk", "if_then": "If stuck, then I will list 3 bullet points."
          }],
          "drop_or_pause": [{ "item": "Side Quest", "action": "pause", "reason": "Over limit" }],
          "question_to_sit_with": "What makes mornings work?",
          "data_caveat": null
        }
        """;

    private static CoachInput SampleInput(params CheckInAnswer[] answers) => new(
        WeekStart: new DateOnly(2026, 9, 27),
        WeekEnd: new DateOnly(2026, 10, 3),
        Review: new WeeklyReview(
            ActiveProjectCount: 4, MaxActiveProjects: 3, IsOverLimit: true,
            TasksCompletedThisWeek: 7, FocusTimeThisWeek: TimeSpan.FromMinutes(375),
            NewProjectsThisWeek: 1, ArchivedProjectsThisWeek: 0,
            ProjectActivity: [new ProjectActivityLine("LTFI", TimeSpan.FromHours(5), 6)],
            StalledProjects: [new StalledProjectLine("Thesis", 15)]),
        Daily: [new DayActivity(new DateOnly(2026, 10, 1), 9)],
        RecentEvidence:
        [
            new EvidenceLine(Guid.NewGuid(), EvidenceType.TaskCompleted, "local", "Ship review page", null,
                null, "LTFI", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero))
        ],
        Answers: answers,
        LastWeekCommitments: ["Finish onboarding flow"],
        CurrentStreakDays: 3);

    [Fact]
    public async Task Prompt_carries_the_numbers_answers_and_schema_and_card_is_parsed()
    {
        var llm = new MockLlmProvider(ValidCard);
        var coach = new CoachService(llm);

        var report = await coach.AnalyzeWeekAsync(SampleInput(
            new CheckInAnswer("What moved forward?", "Got the review page shipped"),
            new CheckInAnswer("Energy 1-5?", "")));

        var req = Assert.Single(llm.Requests);
        Assert.Equal(CoachPrompts.WeeklySystemPrompt, req.SystemPrompt);
        Assert.Equal(CoachPrompts.SchemaName, req.Schema!.Name);
        Assert.Contains("\"tasks_completed\": 7", req.UserContent);
        Assert.Contains("\"focus_hours\": 6.3", req.UserContent);   // 375 min -> 6.3h
        Assert.Contains("\"is_over_limit\": true", req.UserContent);
        Assert.Contains("Thesis", req.UserContent);
        Assert.Contains("Ship review page", req.UserContent);
        Assert.Contains("Finish onboarding flow", req.UserContent);
        Assert.Contains("2026-W40", req.UserContent);
        Assert.Contains("A: Got the review page shipped", req.UserContent);
        Assert.DoesNotContain("Energy 1-5?", req.UserContent);   // blank answers are dropped

        Assert.Equal("Solid week on LTFI, quiet on Thesis.", report.Headline);
        Assert.Single(report.Wins);
        Assert.Equal("helped", report.Patterns[0].Effect);
        var c = Assert.Single(report.Commitments);
        Assert.Equal("Thesis", c.ProjectTitle);
        Assert.Equal(10, c.FirstStepMinutes);   // 25 clamped to the 10-minute rule
        Assert.Equal("pause", report.DropOrPause[0].Action);
        Assert.Null(report.DataCaveat);
        Assert.Equal("mock-model", report.Model);
        Assert.Contains("\"headline\"", report.RawJson);
    }

    [Fact]
    public async Task No_answers_tells_the_coach_to_work_from_data_alone()
    {
        var llm = new MockLlmProvider(ValidCard);
        await new CoachService(llm).AnalyzeWeekAsync(SampleInput());
        Assert.Contains("No check-in answers", llm.Requests[0].UserContent);
    }

    [Theory]
    [InlineData("Sorry, I can't produce JSON today.")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"headline\": ")]
    public async Task Malformed_output_is_a_graceful_error(string reply)
    {
        var coach = new CoachService(new MockLlmProvider(reply));
        var ex = await Assert.ThrowsAsync<LlmException>(() => coach.AnalyzeWeekAsync(SampleInput()));
        Assert.Equal(LlmFailureKind.MalformedOutput, ex.Kind);
    }

    [Fact]
    public void Parse_tolerates_code_fences_missing_fields_and_bad_enums()
    {
        var report = CoachService.Parse(new LlmResponse(
            "```json\n{ \"headline\": \"ok\", \"patterns\": [{ \"observation\": \"x\", \"effect\": \"amazing\" }], " +
            "\"commitments\": [{ \"title\": \"Do it\", \"first_step_minutes\": 0 }] }\n```",
            "m", new LlmUsage(0, 0)));

        Assert.Equal("ok", report.Headline);
        Assert.Empty(report.Wins);
        Assert.Equal("neutral", report.Patterns[0].Effect);
        Assert.Equal(1, report.Commitments[0].FirstStepMinutes);
        Assert.Null(report.Commitments[0].ProjectTitle);
        Assert.Equal(string.Empty, report.QuestionToSitWith);
    }

    [Fact]
    public async Task Unconfigured_coach_fails_fast_without_calling_the_provider()
    {
        var llm = new MockLlmProvider(ValidCard, configured: false);
        var coach = new CoachService(llm);

        Assert.False(coach.IsConfigured);
        var ex = await Assert.ThrowsAsync<LlmException>(() => coach.AnalyzeWeekAsync(SampleInput()));
        Assert.Equal(LlmFailureKind.NotConfigured, ex.Kind);
        Assert.Empty(llm.Requests);
    }

    // ---- OpenAiProvider (fake HTTP transport; never the real API) ----

    private static string ResponsesPayload(string text) => new JsonObject
    {
        ["model"] = "gpt-test-2026",
        ["status"] = "completed",
        ["output"] = new JsonArray
        {
            new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
            new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "output_text", ["text"] = text } }
            }
        },
        ["usage"] = new JsonObject { ["input_tokens"] = 4200, ["output_tokens"] = 1500 }
    }.ToJsonString();

    [Fact]
    public async Task OpenAi_provider_sends_a_strict_schema_request_and_walks_the_output()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ResponsesPayload(ValidCard)));
        var settings = new LlmSettings { Model = "gpt-6.1-sol", BaseUrl = "https://example.test/v1/" };
        var provider = new OpenAiProvider(new HttpClient(handler), settings, new FixedKeyStore("sk-secret"));

        var report = await new CoachService(provider).AnalyzeWeekAsync(SampleInput());

        var (request, body) = Assert.Single(handler.Sent);
        Assert.Equal("https://example.test/v1/responses", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("gpt-6.1-sol", (string?)json["model"]);
        Assert.False((bool)json["store"]!);
        Assert.Equal("low", (string?)json["reasoning"]!["effort"]);
        Assert.Equal("json_schema", (string?)json["text"]!["format"]!["type"]);
        Assert.True((bool)json["text"]!["format"]!["strict"]!);
        Assert.Equal("object", (string?)json["text"]!["format"]!["schema"]!["type"]);

        Assert.Equal("gpt-test-2026", report.Model);
        Assert.Equal(4200, report.Usage.InputTokens);
        Assert.Equal("Draft thesis intro", report.Commitments[0].Title);
    }

    [Fact]
    public async Task OpenAi_provider_retries_once_on_5xx_and_never_leaks_the_key()
    {
        var error = "{\"error\":{\"message\":\"Server melted for key sk-secret\"}}";
        var handler = new FakeHandler((HttpStatusCode.InternalServerError, error), (HttpStatusCode.BadGateway, error));
        var provider = new OpenAiProvider(new HttpClient(handler), new LlmSettings(), new FixedKeyStore("sk-secret"),
            retryDelay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            provider.CompleteAsync(new LlmRequest("sys", "user")));

        Assert.Equal(2, handler.Sent.Count);
        Assert.Equal(LlmFailureKind.Transport, ex.Kind);
        Assert.Contains("502", ex.Message);
        Assert.DoesNotContain("sk-secret", ex.Message);
    }

    [Fact]
    public async Task OpenAi_provider_without_a_key_is_not_configured()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, "{}"));
        var provider = new OpenAiProvider(new HttpClient(handler), new LlmSettings(), new FixedKeyStore(null));

        Assert.False(provider.IsConfigured);
        var ex = await Assert.ThrowsAsync<LlmException>(() => provider.CompleteAsync(new LlmRequest("s", "u")));
        Assert.Equal(LlmFailureKind.NotConfigured, ex.Kind);
        Assert.Contains("OPENAI_API_KEY", ex.Message);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public void Response_parser_surfaces_refusals_and_truncation()
    {
        var refusal = """{"output":[{"type":"message","content":[{"type":"refusal","refusal":"nope"}]}]}""";
        Assert.Equal(LlmFailureKind.Refused,
            Assert.Throws<LlmException>(() => OpenAiProvider.ParseResponse(refusal, "m")).Kind);

        var incomplete = """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""";
        var ex = Assert.Throws<LlmException>(() => OpenAiProvider.ParseResponse(incomplete, "m"));
        Assert.Equal(LlmFailureKind.Incomplete, ex.Kind);
        Assert.Contains("max_output_tokens", ex.Message);
    }

    // ---- key + settings storage ----

    [Fact]
    public void Dpapi_key_store_round_trips_and_env_var_wins()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"ltfi-key-{Guid.NewGuid():N}.key");
        var envVar = $"LTFI_TEST_KEY_{Guid.NewGuid():N}";
        var store = new DpapiApiKeyStore(path, envVar);
        try
        {
            Assert.Null(store.GetKey());

            store.SetKey("  sk-test-123  ");
            Assert.Equal("sk-test-123", store.GetKey());
            Assert.Equal("file", store.KeySource);
            Assert.DoesNotContain("sk-test-123", File.ReadAllText(path));   // encrypted at rest

            Environment.SetEnvironmentVariable(envVar, "sk-from-env");
            Assert.Equal("sk-from-env", store.GetKey());
            Assert.Equal($"env:{envVar}", store.KeySource);
            Environment.SetEnvironmentVariable(envVar, null);

            store.ClearKey();
            Assert.Null(store.GetKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Settings_fall_back_to_defaults_and_merge_partial_files()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltfi-llm-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal("gpt-6.1-sol", LlmSettings.Load(path).Model);   // missing file

            File.WriteAllText(path, "{ \"model\": \"gpt-6-luna\", // cheap\n }");
            var s = LlmSettings.Load(path);
            Assert.Equal("gpt-6-luna", s.Model);
            Assert.Equal("https://api.openai.com/v1", s.BaseUrl);

            File.WriteAllText(path, "not json");
            Assert.Equal("gpt-6.1-sol", LlmSettings.Load(path).Model);   // corrupt file
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
