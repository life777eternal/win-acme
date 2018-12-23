﻿using ACMESharp.Protocol;
using ACMESharp.Protocol.Resources;
using Autofac;
using PKISharp.WACS.Acme;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services;
using PKISharp.WACS.Services.Renewal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading;

namespace PKISharp.WACS
{
    internal partial class Program
    {
        private static IInputService _input;
        private static IRenewalService _renewalService;
        private static IOptionsService _optionsService;
        private static Options _options;
        private static ILogService _log;
        private static IContainer _container;

        private static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        private static void Main(string[] args)
        {
            // Setup DI
            _container = AutofacBuilder.Global(args);

            // Basic services
            _log = _container.Resolve<ILogService>();
            _optionsService = _container.Resolve<IOptionsService>();
            _options = _optionsService.Options;
            if (_options == null) return;
            _input = _container.Resolve<IInputService>();

            // .NET Framework check
            var dn = _container.Resolve<DotNetVersionService>();
            if (!dn.Check())
            {
                return;
            }

            // Show version information
            _input.ShowBanner();

            // Advanced services
            _renewalService = _container.Resolve<IRenewalService>();
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            // Main loop
            do
            {
                try
                {
                    if (_options.Renew)
                    {
                        CheckRenewals(_options.ForceRenewal);
                        CloseDefault();
                    }
                    else if (!string.IsNullOrEmpty(_options.Plugin))
                    {
                        if (_options.Cancel)
                        {
                            CancelRenewal();
                        }
                        else
                        {
                            CreateNewCertificate(RunLevel.Unattended);
                        }
                        CloseDefault();
                    }
                    else
                    {
                        MainMenu();
                    }
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
                if (!_options.CloseOnFinish)
                {
                    _options.Plugin = null;
                    _options.Renew = false;
                    _options.ForceRenewal = false;
                    Environment.ExitCode = 0;
                }
            } while (!_options.CloseOnFinish);
        }

        /// <summary>
        /// Handle exceptions
        /// </summary>
        /// <param name="ex"></param>
        private static void HandleException(Exception ex = null, string message = null)
        {
            if (ex != null)
            {
                _log.Debug($"{ex.GetType().Name}: {{@e}}", ex);
                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    _log.Debug($"Inner: {ex.GetType().Name}: {{@e}}", ex);
                }
                _log.Error($"{ex.GetType().Name}: {{e}}", string.IsNullOrEmpty(message) ? ex.Message : message);
                Environment.ExitCode = ex.HResult;
            }
            else if (!string.IsNullOrEmpty(message))
            {
                _log.Error(message);
                Environment.ExitCode = -1;
            }
        }

        /// <summary>
        /// Present user with the option to close the program
        /// Useful to keep the console output visible when testing
        /// unattended commands
        /// </summary>
        private static void CloseDefault()
        {
            if (_options.Test && !_options.CloseOnFinish)
            {
                _options.CloseOnFinish = _input.PromptYesNo("[--test] Quit?");
            }
            else
            {
                _options.CloseOnFinish = true;
            }
        }

        /// <summary>
        /// Create new ScheduledRenewal from the options
        /// </summary>
        /// <returns></returns>
        private static ScheduledRenewal CreateRenewal(Options options)
        {
            return new ScheduledRenewal
            {
                Binding = new Target
                {
                    TargetPluginName = options.Plugin,
                    ValidationPluginName = string.IsNullOrWhiteSpace(options.Validation) ? null : $"{options.ValidationMode}.{options.Validation}"
                },
                New = true,
                Test = options.Test,
                Script = options.Script,
                ScriptParameters = options.ScriptParameters,
                CentralSslStore = options.CentralSslStore,
                CertificateStore = options.CertificateStore,
                KeepExisting = options.KeepExisting,
                InstallationPluginNames = options.Installation.Any() ? options.Installation.ToList() : null,
                Warmup = options.Warmup
            };
        }

        /// <summary>
        /// If renewal is already Scheduled, replace it with the new options
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        private static ScheduledRenewal CreateRenewal(ScheduledRenewal temp)
        {
            var renewal = _renewalService.Find(temp.Binding);
            if (renewal == null)
            {
                renewal = temp;
            }
            else
            {
                renewal.Updated = true;
            }
            renewal.Test = temp.Test;
            renewal.Binding = temp.Binding;
            renewal.CentralSslStore = temp.CentralSslStore;
            renewal.CertificateStore = temp.CertificateStore;
            renewal.InstallationPluginNames = temp.InstallationPluginNames;
            renewal.KeepExisting = temp.KeepExisting;
            renewal.Script = temp.Script;
            renewal.ScriptParameters = temp.ScriptParameters;
            renewal.Warmup = temp.Warmup;
            return renewal;
        }

        private static void CancelRenewal()
        {
            var tempRenewal = CreateRenewal(_options);
            using (var scope = AutofacBuilder.Renewal(_container, tempRenewal, RunLevel.Unattended))
            {
                // Choose target plugin
                var targetPluginFactory = scope.Resolve<ITargetPluginFactory>();
                if (targetPluginFactory is INull)
                {
                    return; // User cancelled or unable to resolve
                }

                // Aquire target
                var targetPlugin = scope.Resolve<ITargetPlugin>();
                var target = targetPlugin.Default(_optionsService);
                if (target == null)
                {
                    _log.Error("Plugin {name} was unable to generate a target", targetPluginFactory.Name);
                    return;
                }

                // Find renewal
                var renewal = _renewalService.Find(target);
                if (renewal == null)
                {
                    _log.Warning("No renewal scheduled for {target}, this run has no effect", target);
                    return;
                }

                // Cancel renewal
                _renewalService.Cancel(renewal);
            }
        }

        private static void CreateNewCertificate(RunLevel runLevel)
        {
            _log.Information(true, "Running in {runLevel} mode", runLevel);
            var tempRenewal = CreateRenewal(_options);
            using (var scope = AutofacBuilder.Renewal(_container, tempRenewal, runLevel))
            {
                // Choose target plugin
                var targetPluginFactory = scope.Resolve<ITargetPluginFactory>();
                if (targetPluginFactory is INull)
                {
                    HandleException(message: $"No target plugin could be selected");
                    return; // User cancelled or unable to resolve
                }

                // Aquire target
                var targetPlugin = scope.Resolve<ITargetPlugin>();
                var target = runLevel == RunLevel.Unattended ? targetPlugin.Default(_optionsService) : targetPlugin.Aquire(_optionsService, _input, runLevel);
                var originalTarget = tempRenewal.Binding;
                tempRenewal.Binding = target;
                if (target == null)
                {
                    HandleException(message: $"Plugin {targetPluginFactory.Name} was unable to generate a target");
                    return;
                }
                tempRenewal.Binding.TargetPluginName = targetPluginFactory.Name;
                tempRenewal.Binding.SSLPort = _options.SSLPort;
                tempRenewal.Binding.SSLIPAddress = _options.SSLIPAddress;
                tempRenewal.Binding.ValidationPort = _options.ValidationPort;
                tempRenewal.Binding.ValidationPluginName = originalTarget.ValidationPluginName;
                _log.Information("Plugin {name} generated target {target}", targetPluginFactory.Name, tempRenewal.Binding);

                // Choose validation plugin
                var validationPluginFactory = scope.Resolve<IValidationPluginFactory>();
                if (validationPluginFactory is INull)
                {
                    HandleException(message: $"No validation plugin could be selected");
                    return; // User cancelled
                }
                else if (!validationPluginFactory.CanValidate(target))
                {
                    // Might happen in unattended mode
                    HandleException(message: $"Validation plugin {validationPluginFactory.Name} is unable to validate target");
                    return;
                }

                // Configure validation
                try
                {
                    if (runLevel == RunLevel.Unattended)
                    {
                        validationPluginFactory.Default(target, _optionsService);
                    }
                    else
                    {
                        validationPluginFactory.Aquire(target, _optionsService, _input, runLevel);
                    }
                    tempRenewal.Binding.ValidationPluginName = $"{validationPluginFactory.ChallengeType}.{validationPluginFactory.Name}";
                }
                catch (Exception ex)
                {
                    HandleException(ex, "Invalid validation input");
                    return;
                }

                // Choose and configure installation plugins
                try
                {
                    var installFactories = scope.Resolve<List<IInstallationPluginFactory>>();
                    if (installFactories.Count == 0)
                    {
                        // User cancelled, otherwise we would at least have the Null-installer
                        return;
                    }
                    foreach (var installFactory in installFactories)
                    {
                        if (runLevel == RunLevel.Unattended)
                        {
                            installFactory.Default(tempRenewal, _optionsService);
                        }
                        else
                        {
                            installFactory.Aquire(tempRenewal, _optionsService, _input, runLevel);
                        }
                    }
                    tempRenewal.InstallationPluginNames = installFactories.Select(f => f.Name).ToList();
                }
                catch (Exception ex)
                {
                    HandleException(ex, "Invalid installation input");
                    return;
                }

                var renewal = CreateRenewal(tempRenewal);
                var result = Renew(scope, renewal);
                if (!result.Success)
                {
                    HandleException(message: $"Create certificate failed: {result.ErrorMessage}");
                }
                else
                {
                    _renewalService.Save(renewal, result);
                }
            }
        }

        private static RenewResult Renew(ScheduledRenewal renewal, RunLevel runLevel)
        {
            using (var scope = AutofacBuilder.Renewal(_container, renewal, runLevel))
            {
                return Renew(scope, renewal);
            }
        }

        private static RenewResult Renew(ILifetimeScope renewalScope, ScheduledRenewal renewal)
        {
            var targetPlugin = renewalScope.Resolve<ITargetPlugin>();
            var originalBinding = renewal.Binding;
            renewal.Binding = targetPlugin.Refresh(renewal.Binding);
            if (renewal.Binding == null)
            {
                renewal.Binding = originalBinding;
                return new RenewResult("Renewal target not found");
            }
            var split = targetPlugin.Split(renewal.Binding);
            renewal.Binding.AlternativeNames = split.SelectMany(s => s.AlternativeNames).ToList();
            var identifiers = split.SelectMany(t => t.GetHosts(false)).Distinct();
            var client = renewalScope.Resolve<ClientWrapper>();
            var order = client.CreateOrder(identifiers);
            var authorizations = new List<Authorization>();
            foreach (var authUrl in order.Payload.Authorizations)
            {
                authorizations.Add(client.GetAuthorizationDetails(authUrl));
            }
            foreach (var target in split)
            {
                foreach (var identifier in target.GetHosts(false))
                {
                    var authorization = authorizations.FirstOrDefault(a => a.Identifier.Value == identifier);
                    var challenge = Authorize(renewalScope, order, target, authorization);
                    if (challenge.Status != _authorizationValid)
                    {
                        return OnRenewFail(challenge);
                    }
                }
            }
            return OnRenewSuccess(renewalScope, renewal, order);
        }

        /// <summary>
        /// Steps to take on authorization failed
        /// </summary>
        /// <param name="auth"></param>
        /// <returns></returns>
        public static RenewResult OnRenewFail(Challenge challenge)
        {
            var errors = challenge?.Error;
            if (errors != null)
            {
                _log.Error("ACME server reported:");
                _log.Error("{@value}", errors);
            }
            return new RenewResult("Authorization failed");

        }

        /// <summary>
        /// Steps to take on succesful (re)authorization
        /// </summary>
        /// <param name="target"></param>
        private static RenewResult OnRenewSuccess(ILifetimeScope renewalScope, ScheduledRenewal renewal, OrderDetails order)
        {
            RenewResult result = null;
            try
            {
                var certificateService = renewalScope.Resolve<CertificateService>();
                var storePlugin = renewalScope.Resolve<IStorePlugin>();
                var oldCertificate = renewal.Certificate(storePlugin);
                var newCertificate = certificateService.RequestCertificate(renewal.Binding, order);

                // Test if a new certificate has been generated 
                if (newCertificate == null)
                {
                    return new RenewResult("No certificate generated");
                }
                else
                {
                    result = new RenewResult(newCertificate);
                }

                // Early escape for testing validation only
                if (_options.Test &&
                    renewal.New &&
                    !_input.PromptYesNo($"[--test] Do you want to install the certificate?"))
                    return result;

                try
                {
                    // Check if the newly requested certificate is already in the store, 
                    // which might be the case due to the cache mechanism built into the 
                    // RequestCertificate function
                    var storedCertificate = storePlugin.FindByThumbprint(newCertificate.Certificate.Thumbprint);
                    if (storedCertificate != null)
                    {
                        // Copy relevant properties
                        _log.Warning("Certificate with thumbprint {thumbprint} is already in the store", newCertificate.Certificate.Thumbprint);
                        newCertificate.Store = storedCertificate.Store;
                    }
                    else
                    {
                        // Save to store
                        storePlugin.Save(newCertificate);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unable to store certificate");
                    result.Success = false;
                    result.ErrorMessage = $"Store failed: {ex.Message}";
                    return result;
                }

                // Run installation plugin(s)
                try
                {
                    var installFactories = renewalScope.Resolve<List<IInstallationPluginFactory>>();
                    var steps = installFactories.Count();
                    for (var i = 0; i < steps; i++)
                    {
                        var installFactory = installFactories[i];
                        if (!(installFactory is INull))
                        {
                            var installInstance = (IInstallationPlugin)renewalScope.Resolve(installFactory.Instance);
                            if (steps > 1)
                            {
                                _log.Information("Installation step {n}/{m}: {name}...", i + 1, steps, installFactory.Name);
                            }
                            else
                            {
                                _log.Information("Installing with {name}...", installFactory.Name);
                            }
                            installInstance.Install(newCertificate, oldCertificate);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unable to install certificate");
                    result.Success = false;
                    result.ErrorMessage = $"Install failed: {ex.Message}";
                }

                // Delete the old certificate if not forbidden, found and not re-used
                if ((!renewal.KeepExisting ?? false) &&
                    oldCertificate != null &&
                    newCertificate.Certificate.Thumbprint != oldCertificate.Certificate.Thumbprint)
                {
                    try
                    {
                        storePlugin.Delete(oldCertificate);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Unable to delete previous certificate");
                        //result.Success = false; // not a show-stopper, consider the renewal a success
                        result.ErrorMessage = $"Delete failed: {ex.Message}";
                    }
                }

                // Add or update renewal
                if (renewal.New &&
                    !_options.NoTaskScheduler &&
                    (!_options.Test ||
                    _input.PromptYesNo($"[--test] Do you want to automatically renew this certificate?")))
                {
                    var taskScheduler = renewalScope.Resolve<TaskSchedulerService>();
                    taskScheduler.EnsureTaskScheduler();
                }

                return result;
            }
            catch (Exception ex)
            {
                // Result might still contain the Thumbprint of the certificate 
                // that was requested and (partially? installed, which might help
                // with debugging
                if (result == null)
                {
                    result = new RenewResult(ex.Message);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                }
            }

            return result;
        }

        /// <summary>
        /// Loop through the store renewals and run those which are
        /// due to be run
        /// </summary>
        private static void CheckRenewals(bool force)
        {
            _log.Verbose("Checking renewals");
            var renewals = _renewalService.Renewals.ToList();
            if (renewals.Count == 0)
                _log.Warning("No scheduled renewals found.");

            var now = DateTime.UtcNow;
            foreach (var renewal in renewals)
            {
                if (force)
                {
                    ProcessRenewal(renewal);
                }
                else
                {
                    _log.Verbose("Checking {renewal}", renewal.Binding.Host);
                    if (renewal.Date >= now)
                    {
                        _log.Information(true, "Renewal for certificate {renewal} is due after {date}", renewal.Binding.Host, renewal.Date.ToUserString());
                    }
                    else
                    {
                        ProcessRenewal(renewal);
                    }
                }
            }
        }

        /// <summary>
        /// Process a single renewal
        /// </summary>
        /// <param name="renewal"></param>
        private static void ProcessRenewal(ScheduledRenewal renewal)
        {
            _log.Information(true, "Renewing certificate for {renewal}", renewal.Binding.Host);
            try
            {
                // Let the plugin run
                var result = Renew(renewal, RunLevel.Unattended);
                _renewalService.Save(renewal, result);
            }
            catch (Exception ex)
            {
                HandleException(ex);
                _log.Error("Renewal for {host} failed, will retry on next run", renewal.Binding.Host);
            }
        }

        private const string _authorizationValid = "valid";
        private const string _authorizationPending = "pending";
        private const string _authorizationInvalid = "invalid";

        /// <summary>
        /// Make sure we have authorization for every host in target
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        private static Challenge Authorize(ILifetimeScope renewalScope, OrderDetails order, Target target, Authorization authorization)
        {
            var invalid = new Challenge { Status = _authorizationInvalid };
            var valid = new Challenge { Status = _authorizationValid };
            var client = renewalScope.Resolve<ClientWrapper>();
            var identifier = authorization.Identifier.Value;
            try
            {
                _log.Information("Authorize identifier: {identifier}", identifier);
                if (authorization.Status == _authorizationValid && !_options.Test)
                {
                    _log.Information("Cached authorization result: {Status}", authorization.Status);
                    return valid;
                }
                else
                {
                    using (var identifierScope = AutofacBuilder.Identifier(renewalScope, target, identifier))
                    {
                        IValidationPluginFactory validationPluginFactory = null;
                        IValidationPlugin validationPlugin = null;
                        try
                        {
                            validationPluginFactory = identifierScope.Resolve<IValidationPluginFactory>();
                            validationPlugin = identifierScope.Resolve<IValidationPlugin>();
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex, "Error resolving validation plugin");
                        }
                        if (validationPluginFactory == null || validationPluginFactory is INull || validationPlugin == null)
                        {
                            _log.Error("Validation plugin not found or not created.");
                            return invalid;
                        }
                        var challenge = authorization.Challenges.FirstOrDefault(c => c.Type == validationPluginFactory.ChallengeType);
                        if (challenge == null)
                        {
                            _log.Error("Expected challenge type {type} not available for {identifier}.",
                                validationPluginFactory.ChallengeType,
                                authorization.Identifier.Value);
                            return invalid;
                        }

                        if (challenge.Status == _authorizationValid)
                        {
                            _log.Information("{dnsIdentifier} already validated by {challengeType} validation ({name})",
                                 authorization.Identifier.Value,
                                 validationPluginFactory.ChallengeType,
                                 validationPluginFactory.Name);
                            return valid;
                        }

                        _log.Information("Authorizing {dnsIdentifier} using {challengeType} validation ({name})", 
                            identifier, 
                            validationPluginFactory.ChallengeType, 
                            validationPluginFactory.Name);
                        try
                        {
                            var details = client.GetChallengeDetails(authorization, challenge);
                            validationPlugin.PrepareChallenge(details);
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex, "Error preparing for challenge answer");
                            return invalid;
                        }

                        _log.Debug("Submitting challenge answer");
                        challenge = client.SubmitChallengeAnswer(challenge);

                        // Have to loop to wait for server to stop being pending
                        var tries = 0;
                        var maxTries = 4;
                        while (challenge.Status == _authorizationPending)
                        {
                            _log.Debug("Refreshing authorization");
                            Thread.Sleep(2000); // this has to be here to give ACME server a chance to think
                            challenge = client.DecodeChallenge(challenge.Url);
                            tries += 1;
                            if (tries > maxTries)
                            {
                                _log.Error("Authorization timed out");
                                return invalid;
                            }
                        }

                        if (challenge.Status != _authorizationValid)
                        {
                            _log.Error("Authorization result: {Status}", challenge.Status);
                            return invalid;
                        }
                        else
                        {
                            _log.Information("Authorization result: {Status}", challenge.Status);
                            return valid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Error authorizing {target}", target);
                HandleException(ex);
                return invalid;
            }
        }
    }
}