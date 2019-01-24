﻿namespace DW.ELA.Plugin.EDSM
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using DW.ELA.Controller;
    using DW.ELA.Interfaces;
    using DW.ELA.Interfaces.Settings;
    using DW.ELA.Utility;
    using MoreLinq;
    using Newtonsoft.Json.Linq;
    using NLog;
    using NLog.Fluent;

    public class EdsmPlugin : AbstractPlugin<JObject, EdsmSettings>
    {
        private static readonly ILogger Log = LogManager.GetCurrentClassLogger();
        private readonly IRestClient restClient = new ThrottlingRestClient("https://www.edsm.net/api-journal-v1/");
        private readonly Task<HashSet<string>> ignoredEvents;
        private readonly IPlayerStateHistoryRecorder playerStateRecorder;
        private readonly IEventConverter<JObject> eventConverter;
        private IEdsmApiFacade apiFacade;
        public const string CPluginId = "EdsmUploader";

        public EdsmPlugin(ISettingsProvider settingsProvider, IPlayerStateHistoryRecorder playerStateRecorder)
            : base(settingsProvider)
        {
            this.playerStateRecorder = playerStateRecorder ?? throw new ArgumentNullException(nameof(playerStateRecorder));
            eventConverter = new EdsmEventConverter(playerStateRecorder);
            ignoredEvents =
                 restClient.GetAsync("discard")
                    .ContinueWith((t) => t.IsFaulted ? new HashSet<string>() : new HashSet<string>(JArray.Parse(t.Result).ToObject<string[]>()));

            settingsProvider.SettingsChanged += (o, e) => ReloadSettings();
            ReloadSettings();
        }

        protected override TimeSpan FlushInterval => TimeSpan.FromMinutes(1);

        protected override IEventConverter<JObject> EventConverter => eventConverter;

        public override void ReloadSettings()
        {
            FlushQueue();
            apiFacade = new EdsmApiFacade(restClient, GlobalSettings.CommanderName, Settings.ApiKey);
        }

        public override async void FlushEvents(ICollection<JObject> events)
        {
            if (!Settings.Verified)
                return;
            try
            {
                var apiEventsBatches = events
                    .Where(e => !ignoredEvents.Result.Contains(e["event"].ToString()))
                    .TakeLast(3000) // Limit to last N events to avoid EDSM overload
                    .Reverse()
                    .Batch(100) // EDSM API only accepts 100 events in single call
                    .ToList();
                foreach (var batch in apiEventsBatches)
                    await apiFacade?.PostLogEvents(batch.ToArray());
                Log.Info()
                    .Message("Uploaded {0} events", events.Count)
                    .Property("eventsCount", events.Count)
                    .Write();
            }
            catch (Exception e)
            {
                Log.Error(e, "Error while processing events");
            }
        }

        public override string PluginName => "EDSM";

        public override string PluginId => CPluginId;

        public override AbstractSettingsControl GetPluginSettingsControl(GlobalSettings settings) => new EdsmSettingsControl() { GlobalSettings = settings };

    }
}
