﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Domain.Tasks;
using Nop.Plugin.Misc.MailChimp.Domain;
using Nop.Plugin.Misc.MailChimp.Models;
using Nop.Plugin.Misc.MailChimp.Services;
using Nop.Services.Caching;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Messages;
using Nop.Services.Stores;
using Nop.Services.Tasks;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Misc.MailChimp.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class MailChimpController : BasePluginController
    {
        #region Fields

        private readonly ICacheKeyService _cacheKeyService;
        private readonly ILocalizationService _localizationService;
        private readonly INotificationService _notificationService;
        private readonly IScheduleTaskService _scheduleTaskService;
        private readonly ISettingService _settingService;
        private readonly IStaticCacheManager _staticCacheManager;
        private readonly IStoreContext _storeContext;
        private readonly IStoreService _storeService;
        private readonly ISynchronizationRecordService _synchronizationRecordService;
        private readonly MailChimpManager _mailChimpManager;

        #endregion

        #region Ctor

        public MailChimpController(
            ICacheKeyService cacheKeyService,
            ILocalizationService localizationService,
            INotificationService notificationService,
            IScheduleTaskService scheduleTaskService,
            ISettingService settingService,
            IStaticCacheManager cacheManager,
            IStoreContext storeContext,
            IStoreService storeService,
            ISynchronizationRecordService synchronizationRecordService,
            MailChimpManager mailChimpManager)
        {
            _cacheKeyService = cacheKeyService;
            _localizationService = localizationService;
            _notificationService = notificationService;
            _scheduleTaskService = scheduleTaskService;
            _settingService = settingService;
            _staticCacheManager = cacheManager;
            _storeContext = storeContext;
            _storeService = storeService;
            _synchronizationRecordService = synchronizationRecordService;
            _mailChimpManager = mailChimpManager;
        }

        #endregion

        #region Methods

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure()
        {
            //load settings for a chosen store scope
            var storeId = _storeContext.ActiveStoreScopeConfiguration;
            var mailChimpSettings = _settingService.LoadSetting<MailChimpSettings>(storeId);

            //prepare model
            var model = new ConfigurationModel
            {
                ApiKey = mailChimpSettings.ApiKey,
                PassEcommerceData = mailChimpSettings.PassEcommerceData,
                ListId = mailChimpSettings.ListId,
                ListId_OverrideForStore = storeId > 0 && _settingService.SettingExists(mailChimpSettings, settings => settings.ListId, storeId),
                ActiveStoreScopeConfiguration = storeId
            };

            //check whether synchronization is in progress
            model.SynchronizationStarted = _staticCacheManager.Get<int?>(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.OperationNumberCacheKey), () => null).HasValue;

            //prepare account info
            if (!string.IsNullOrEmpty(mailChimpSettings.ApiKey))
                model.AccountInfo = await _mailChimpManager.GetAccountInfo();

            //prepare available lists
            if (!string.IsNullOrEmpty(mailChimpSettings.ApiKey))
                model.AvailableLists = await _mailChimpManager.GetAvailableLists() ?? new List<SelectListItem>();

            var defaultListId = mailChimpSettings.ListId;
            if (!model.AvailableLists.Any())
            {
                //add the special item for 'there are no lists' with empty guid value
                model.AvailableLists.Add(new SelectListItem
                {
                    Text = _localizationService.GetResource("Plugins.Misc.MailChimp.Fields.List.NotExist"),
                    Value = Guid.Empty.ToString()
                });
                defaultListId = Guid.Empty.ToString();
            }
            else if (string.IsNullOrEmpty(mailChimpSettings.ListId) || mailChimpSettings.ListId.Equals(Guid.Empty.ToString()))
                defaultListId = model.AvailableLists.FirstOrDefault()?.Value;

            //set the default list
            model.ListId = defaultListId;
            mailChimpSettings.ListId = defaultListId;
            _settingService.SaveSettingOverridablePerStore(mailChimpSettings, settings => settings.ListId, model.ListId_OverrideForStore, storeId);

            //synchronization task
            var task = _scheduleTaskService.GetTaskByType(MailChimpDefaults.SynchronizationTask);
            if (task != null)
            {
                model.SynchronizationPeriod = task.Seconds / 60 / 60;
                model.AutoSynchronization = task.Enabled;
            }

            return View("~/Plugins/Misc.MailChimp/Views/Configure.cshtml", model);
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("save")]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (!ModelState.IsValid)
                return await Configure();

            //load settings for a chosen store scope
            var storeId = _storeContext.ActiveStoreScopeConfiguration;
            var mailChimpSettings = _settingService.LoadSetting<MailChimpSettings>(storeId);

            //update stores if the list was changed
            if (!string.IsNullOrEmpty(model.ListId) && !model.ListId.Equals(Guid.Empty.ToString()) && !model.ListId.Equals(mailChimpSettings.ListId))
            {
                (storeId > 0 ? new[] { storeId } : _storeService.GetAllStores().Select(store => store.Id)).ToList()
                    .ForEach(id => _synchronizationRecordService.CreateOrUpdateRecord(EntityType.Store, id, OperationType.Update));
            }

            //prepare webhook
            if (!string.IsNullOrEmpty(mailChimpSettings.ApiKey))
            {
                var listId = !string.IsNullOrEmpty(model.ListId) && !model.ListId.Equals(Guid.Empty.ToString()) ? model.ListId : string.Empty;
                var webhookPrepared = await _mailChimpManager.PrepareWebhook(listId);

                //display warning if webhook is not prepared
                if (!webhookPrepared && !string.IsNullOrEmpty(listId))
                    _notificationService.WarningNotification(_localizationService.GetResource("Plugins.Misc.MailChimp.Webhook.Warning"));
            }

            //save settings
            mailChimpSettings.ApiKey = model.ApiKey;
            mailChimpSettings.PassEcommerceData = model.PassEcommerceData;
            mailChimpSettings.ListId = model.ListId;
            _settingService.SaveSetting(mailChimpSettings, x => x.ApiKey, clearCache: false);
            _settingService.SaveSetting(mailChimpSettings, x => x.PassEcommerceData, clearCache: false);
            _settingService.SaveSettingOverridablePerStore(mailChimpSettings, x => x.ListId, model.ListId_OverrideForStore, storeId, false);
            _settingService.ClearCache();

            //create or update synchronization task
            var task = _scheduleTaskService.GetTaskByType(MailChimpDefaults.SynchronizationTask);
            if (task == null)
            {
                task = new ScheduleTask
                {
                    Type = MailChimpDefaults.SynchronizationTask,
                    Name = MailChimpDefaults.SynchronizationTaskName,
                    Seconds = MailChimpDefaults.DefaultSynchronizationPeriod * 60 * 60
                };
                _scheduleTaskService.InsertTask(task);
            }

            var synchronizationPeriodInSeconds = model.SynchronizationPeriod * 60 * 60;
            var synchronizationEnabled = model.AutoSynchronization;
            if (task.Enabled != synchronizationEnabled || task.Seconds != synchronizationPeriodInSeconds)
            {
                //task parameters was changed
                task.Enabled = synchronizationEnabled;
                task.Seconds = synchronizationPeriodInSeconds;
                _scheduleTaskService.UpdateTask(task);
                _notificationService.WarningNotification(_localizationService.GetResource("Plugins.Misc.MailChimp.Fields.AutoSynchronization.Restart"));
            }

            _notificationService.SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));

            return await Configure();
        }

        [HttpPost, ActionName("Configure")]
        [FormValueRequired("synchronization")]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Synchronization()
        {
            //ensure that user list for the synchronization is selected
            var mailChimpSettings = _settingService.LoadSetting<MailChimpSettings>();
            if (string.IsNullOrEmpty(mailChimpSettings.ListId) || mailChimpSettings.ListId.Equals(Guid.Empty.ToString()))
            {
                _notificationService.ErrorNotification(_localizationService.GetResource("Plugins.Misc.MailChimp.Synchronization.Error"));
                return await Configure();
            }

            //start the synchronization
            var operationNumber = await _mailChimpManager.Synchronize(true);
            if (operationNumber.HasValue)
            {
                //cache number of operations
                _staticCacheManager.Remove(MailChimpDefaults.SynchronizationBatchesCacheKey);
                _staticCacheManager.Set(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.OperationNumberCacheKey), operationNumber.Value);

                _notificationService.SuccessNotification(_localizationService.GetResource("Plugins.Misc.MailChimp.Synchronization.Started"));
            }
            else
                _notificationService.ErrorNotification(_localizationService.GetResource("Plugins.Misc.MailChimp.Synchronization.Error"));

            return await Configure();
        }

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public IActionResult IsSynchronizationComplete()
        {
            //try to get number of operations and already handled batches
            var operationNumber = _staticCacheManager.Get<int?>(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.OperationNumberCacheKey), () => null);
            var batchesInfo = _staticCacheManager.Get(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.SynchronizationBatchesCacheKey), () => new Dictionary<string, int>());

            //check whether the synchronization is finished
            if (!operationNumber.HasValue || operationNumber.Value == batchesInfo.Values.Sum())
            {
                //clear cached values
                _staticCacheManager.Remove(MailChimpDefaults.OperationNumberCacheKey);
                _staticCacheManager.Remove(MailChimpDefaults.SynchronizationBatchesCacheKey);

                return Json(true);
            }

            return new NullJsonResult();
        }

        public IActionResult BatchWebhook()
        {
            return Ok();
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> BatchWebhook(IFormCollection form)
        {
            if (!Request.Form?.Any() ?? true)
                return BadRequest();

            //try to get already handled batches
            var batchesInfo = _staticCacheManager.Get(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.SynchronizationBatchesCacheKey), () => new Dictionary<string, int>());

            //handle batch webhook
            var (id, completedOperationNumber) = await _mailChimpManager.HandleBatchWebhook(Request.Form, batchesInfo);
            if (!string.IsNullOrEmpty(id) && completedOperationNumber.HasValue)
            {
                if (!batchesInfo.ContainsKey(id))
                {
                    //update cached value
                    batchesInfo.Add(id, completedOperationNumber.Value);
                    _staticCacheManager.Set(_cacheKeyService.PrepareKeyForDefaultCache(MailChimpDefaults.SynchronizationBatchesCacheKey), batchesInfo);
                }
                return Ok();
            }

            return BadRequest();
        }

        public IActionResult WebHook()
        {
            return Ok();
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> WebHook(IFormCollection form)
        {
            if (!Request.Form?.Any() ?? true)
                return BadRequest();

            //handle webhook
            var success = await _mailChimpManager.HandleWebhook(Request.Form);
            return success ? Ok() : (IActionResult)BadRequest();
        }

        #endregion
    }
}