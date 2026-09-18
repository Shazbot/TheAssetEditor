using Microsoft.Extensions.DependencyInjection;
using Shared.Core.DependencyInjection;
using Shared.GameFormats.AnimationMeta.Parsing;

namespace AssetEditor.Services;

internal sealed class GameFormatsDependencyInjectionContainer : DependencyContainer
{
    public override void Register(IServiceCollection services)
    {
        services.AddSingleton<IMetaDataDatabase, MetaDataDatabase>();
        services.AddTransient<MetaDataFileParser>();
    }
}
