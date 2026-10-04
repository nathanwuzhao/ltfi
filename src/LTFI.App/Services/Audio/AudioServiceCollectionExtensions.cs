using Microsoft.Extensions.DependencyInjection;

namespace LTFI.Services.Audio;

public static class AudioServiceCollectionExtensions
{
    /// <summary>
    /// Two independent players (NSDR track vs. chimes, so a ping never stops the track), the
    /// chime service and the attention alert. Swap <see cref="NetCoreAudioPlayer"/> here.
    /// </summary>
    public static IServiceCollection AddLtfiAudio(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IAudioPlayer>(AudioPlayers.Ambient, (_, _) => new NetCoreAudioPlayer(AudioPlayers.Ambient));
        services.AddKeyedSingleton<IAudioPlayer>(AudioPlayers.Notifications, (_, _) => new NetCoreAudioPlayer(AudioPlayers.Notifications));
        services.AddSingleton<NotificationSounds>();
        services.AddSingleton<AttentionAlert>();
        return services;
    }
}
