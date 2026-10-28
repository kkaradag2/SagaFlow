using Microsoft.AspNetCore.SignalR;

namespace SagaFlow.Hosting.Dashboard;

/// <summary>Read-only notification channel. Current state remains available through the Dashboard API.</summary>
public sealed class SagaFlowHub : Hub;
