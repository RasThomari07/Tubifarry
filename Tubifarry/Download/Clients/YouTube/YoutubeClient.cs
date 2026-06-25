using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Requests;
using Tubifarry.Core.Model;
using Tubifarry.Core.Records;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Download.Clients.YouTube
{
    public class YoutubeClient : DownloadClientBase<YoutubeProviderSettings>
    {
        private readonly IYoutubeDownloadManager _dlManager;
        private readonly INamingConfigService _naminService;

        public YoutubeClient(IYoutubeDownloadManager dlManager, IConfigService configService, IDiskProvider diskProvider, INamingConfigService namingConfigService, IRemotePathMappingService remotePathMappingService, ILocalizationService localizationService, Logger logger) : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _dlManager = dlManager;
            _naminService = namingConfigService;
            RequestHandler.MainRequestHandlers[1].MaxParallelism = 1;
        }

        public override string Name => "Youtube";

        public override string Protocol => nameof(YoutubeDownloadProtocol);

        public new YoutubeProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => _dlManager.Download(remoteAlbum, indexer, _naminService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => _dlManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _dlManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = [new OsPath(Settings.DownloadPath)]
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                TrustedSessionHelper.ValidateAuthenticationSettingsAsync(Settings.TrustedSessionGeneratorUrl, Settings.CookiePath).GetAwaiter().GetResult();
                SessionTokens session = TrustedSessionHelper.GetTrustedSessionTokensAsync(Settings.TrustedSessionGeneratorUrl, true).GetAwaiter().GetResult();
                if (!session.IsValid && !session.IsEmpty)
                    failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", "Failed to retrieve valid tokens from the session generator service"));
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("TrustedSessionGeneratorUrl", $"Failed to valiate session generator service: {ex.Message}"));
            }

            if (string.IsNullOrEmpty(Settings.DownloadPath))
                failures.AddRange(PermissionTester.TestAllPermissions(Settings.FFmpegPath, _logger));
            failures.AddIfNotNull(TestFFmpeg().GetAwaiter().GetResult());
        }

        public async Task<ValidationFailure> TestFFmpeg()
        {
            if (Settings.ReEncode == (int)ReEncodeOptions.Disabled && !Settings.UseSponsorBlock && !Settings.UseYtDlp)
                return null!;

            FfmpegLocator.Invalidate();
            string? resolved = await FfmpegLocator.ResolveAsync(Settings.FFmpegPath);
            AudioMetadataHandler.ResetFFmpegInstallationCheck();

            if (resolved == null)
                return new ValidationFailure("FFmpegInstallation",
                    "FFmpeg not found and auto-download failed. Set the FFmpeg path or ensure internet access.");

            return null!;
        }
    }
}