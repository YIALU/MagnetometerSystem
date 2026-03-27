namespace MagnetometerSystem.Infrastructure.Services;

public interface IUserPreferencesService
{
    Task<T?> GetPreferenceAsync<T>(string key);
    Task SetPreferenceAsync<T>(string key, T value);
}
