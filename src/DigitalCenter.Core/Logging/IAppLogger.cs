using Microsoft.EntityFrameworkCore;

namespace DigitalCenter.Core.Logging;

/// <summary>Minimal logging abstraction so services stay testable without a logging framework.</summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
