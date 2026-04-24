namespace NTG.Agent.Orchestrator.Services.Quota;

public interface IUserQuotaService
{
    Task<QuotaCheckResult> CheckQuotaAsync(Guid? userId, Guid? sessionId, string promptText);
}
