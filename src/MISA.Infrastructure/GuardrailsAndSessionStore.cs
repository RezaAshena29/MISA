using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json;
using MISA.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MISA.Infrastructure;

/// <summary>
/// Guard options loaded from configuration.
/// </summary>
public sealed class GuardOptions
{
	/// <summary>
	/// Maximum accepted prompt length before hard block.
	/// </summary>
	public int MaxPromptLength { get; set; } = 4000;

	/// <summary>
	/// Terms or expressions to block in inbound prompts.
	/// </summary>
	public string[] BlockedPromptPatterns { get; set; } =
	[
		"ignore\\s+previous\\s+instructions",
		"system\\s+prompt",
		"developer\\s+message",
		"exfiltrate"
	];

	/// <summary>
	/// Whether prompt payloads that resemble inline secrets are blocked.
	/// </summary>
	public bool BlockPromptWithInlineSecrets { get; set; } = true;

	/// <summary>
	/// Terms to mask in outbound responses.
	/// </summary>
	public string[] BlockedResponseTerms { get; set; } =
	[
		"internal only",
		"secret"
	];

	/// <summary>
	/// Replacement token for masked terms.
	/// </summary>
	public string MaskToken { get; set; } = "[redacted]";

	/// <summary>
	/// Whether to mask email addresses in outbound content.
	/// </summary>
	public bool MaskEmails { get; set; } = true;

	/// <summary>
	/// Whether to mask phone-like numeric patterns in outbound content.
	/// </summary>
	public bool MaskPhoneNumbers { get; set; } = true;
}

/// <summary>
/// Session store persistence settings.
/// </summary>
public sealed class SessionStoreOptions
{
	/// <summary>
	/// Store mode: InMemory (default) or File.
	/// </summary>
	public string Mode { get; set; } = "InMemory";

	/// <summary>
	/// File path used when Mode is File.
	/// </summary>
	public string FilePath { get; set; } = Path.Combine(Path.GetTempPath(), "misa-agentic-sessions.json");
}

/// <summary>
/// Regex-based inbound prompt guard.
/// </summary>
public sealed class DefaultPromptGuard : IPromptGuard
{
	private static readonly Regex InlineSecretPattern = new(
		@"\b(?:api[_-]?key|token|password|secret)\b\s*(?:=|:)?\s*[A-Za-z0-9_\-]{8,}",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private readonly GuardOptions _options;
	private readonly Regex[] _patterns;

	/// <summary>
	/// Creates a guard with configured block patterns.
	/// </summary>
	public DefaultPromptGuard(IOptions<GuardOptions> options)
	{
		_options = options.Value;
		_patterns = _options.BlockedPromptPatterns
			.Where(pattern => !string.IsNullOrWhiteSpace(pattern))
			.Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
			.ToArray();
	}

	/// <inheritdoc />
	public bool IsSafe(string prompt, out string? violationReason)
	{
		if (string.IsNullOrWhiteSpace(prompt))
		{
			violationReason = "prompt is empty";
			return false;
		}

		if (_options.MaxPromptLength > 0 && prompt.Length > _options.MaxPromptLength)
		{
			violationReason = $"prompt exceeds max length {_options.MaxPromptLength}";
			return false;
		}

		if (_options.BlockPromptWithInlineSecrets && InlineSecretPattern.IsMatch(prompt))
		{
			violationReason = "prompt appears to contain inline secret material";
			return false;
		}

		foreach (var pattern in _patterns)
		{
			if (!pattern.IsMatch(prompt))
			{
				continue;
			}

			violationReason = $"matched pattern '{pattern}'";
			return false;
		}

		violationReason = null;
		return true;
	}
}

/// <summary>
/// Term-masking outbound response guard.
/// </summary>
public sealed class DefaultResponseGuard : IResponseGuard
{
	private static readonly Regex EmailPattern = new(
		@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static readonly Regex PhonePattern = new(
		@"(?<!\d)(?:\+?\d[\s-]?){8,15}(?!\d)",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private readonly GuardOptions _options;

	/// <summary>
	/// Creates a response guard.
	/// </summary>
	public DefaultResponseGuard(IOptions<GuardOptions> options)
	{
		_options = options.Value;
	}

	/// <inheritdoc />
	public string Sanitize(string text)
	{
		var result = text;
		foreach (var blockedTerm in _options.BlockedResponseTerms)
		{
			if (string.IsNullOrWhiteSpace(blockedTerm))
			{
				continue;
			}

			result = Regex.Replace(
				result,
				Regex.Escape(blockedTerm),
				_options.MaskToken,
				RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}

		if (_options.MaskEmails)
		{
			result = EmailPattern.Replace(result, "[redacted-email]");
		}

		if (_options.MaskPhoneNumbers)
		{
			result = PhonePattern.Replace(result, "[redacted-phone]");
		}

		return result;
	}
}

/// <summary>
/// Registers infrastructure services.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
	/// <summary>
	/// Adds infrastructure services and guard policies.
	/// </summary>
	public static IServiceCollection AddMisaInfrastructure(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		services.AddMisaMcp(configuration);
		services.AddOptions<GuardOptions>().Bind(configuration.GetSection("Misa:Guard"));
		services.AddOptions<SessionStoreOptions>().Bind(configuration.GetSection("Misa:SessionStore"));
		services.AddSingleton<IPromptGuard, DefaultPromptGuard>();
		services.AddSingleton<IResponseGuard, DefaultResponseGuard>();
		services.AddSingleton<InMemoryChatSessionStore>();
		services.AddSingleton<FileBackedChatSessionStore>();
		services.AddSingleton<IChatSessionStore>(sp =>
		{
			var options = sp.GetRequiredService<IOptions<SessionStoreOptions>>().Value;
			return string.Equals(options.Mode, "File", StringComparison.OrdinalIgnoreCase)
				? sp.GetRequiredService<FileBackedChatSessionStore>()
				: sp.GetRequiredService<InMemoryChatSessionStore>();
		});
		return services;
	}
}

/// <summary>
/// File-backed session store for local durable workflow continuity.
/// </summary>
public sealed class FileBackedChatSessionStore : IChatSessionStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private readonly ConcurrentDictionary<string, ChatSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _filePath;
	private readonly object _sync = new();

	/// <summary>
	/// Creates a durable session store.
	/// </summary>
	public FileBackedChatSessionStore(IOptions<SessionStoreOptions> options)
	{
		_filePath = options.Value.FilePath;
		LoadFromDisk();
	}

	/// <inheritdoc />
	public Task<ChatSessionState?> GetAsync(string sessionId, CancellationToken cancellationToken)
	{
		_sessions.TryGetValue(sessionId, out var state);
		return Task.FromResult(state);
	}

	/// <inheritdoc />
	public Task SaveAsync(ChatSessionState state, CancellationToken cancellationToken)
	{
		lock (_sync)
		{
			_sessions[state.SessionId] = state with { UpdatedAt = DateTimeOffset.UtcNow };
			PersistToDisk();
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public bool Clear(string sessionId)
	{
		lock (_sync)
		{
			var removed = _sessions.TryRemove(sessionId, out _);
			if (removed)
			{
				PersistToDisk();
			}

			return removed;
		}
	}

	private void LoadFromDisk()
	{
		if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
		{
			return;
		}

		try
		{
			var payload = File.ReadAllText(_filePath);
			var snapshots = JsonSerializer.Deserialize<List<ChatSessionState>>(payload, JsonOptions);
			if (snapshots is null)
			{
				return;
			}

			foreach (var snapshot in snapshots)
			{
				_sessions[snapshot.SessionId] = snapshot;
			}
		}
		catch
		{
			// The store falls back to empty in-memory state if persisted content is unreadable.
		}
	}

	private void PersistToDisk()
	{
		if (string.IsNullOrWhiteSpace(_filePath))
		{
			return;
		}

		var folder = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrWhiteSpace(folder))
		{
			Directory.CreateDirectory(folder);
		}

		var snapshot = _sessions.Values
			.OrderBy(value => value.SessionId, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
		File.WriteAllText(_filePath, payload);
	}
}

/// <summary>
/// In-memory chat session store used for bootstrap and local development.
/// </summary>
public sealed class InMemoryChatSessionStore : IChatSessionStore
{
	private readonly ConcurrentDictionary<string, ChatSessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public Task<ChatSessionState?> GetAsync(string sessionId, CancellationToken cancellationToken)
	{
		_sessions.TryGetValue(sessionId, out var state);
		return Task.FromResult(state);
	}

	/// <inheritdoc />
	public Task SaveAsync(ChatSessionState state, CancellationToken cancellationToken)
	{
		_sessions[state.SessionId] = state with { UpdatedAt = DateTimeOffset.UtcNow };
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public bool Clear(string sessionId)
	{
		return _sessions.TryRemove(sessionId, out _);
	}
}
