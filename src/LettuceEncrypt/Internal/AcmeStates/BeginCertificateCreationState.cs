// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LettuceEncrypt.Internal.AcmeStates
{
    internal class BeginCertificateCreationState : AcmeState
    {
        private readonly ILogger<ServerStartupState> _logger;
        private readonly AcmeCertificateFactory _acmeCertificateFactory;
        private readonly CertificateSelector _selector;
        private readonly IEnumerable<ICertificateRepository> _certificateRepositories;
        private readonly LettuceEncryptDomains _domains;

        public BeginCertificateCreationState(AcmeStateMachineContext context, ILogger<ServerStartupState> logger,
            AcmeCertificateFactory acmeCertificateFactory, CertificateSelector selector,
            IEnumerable<ICertificateRepository> certificateRepositories, LettuceEncryptDomains domains) : base(context)
        {
            _logger = logger;
            _acmeCertificateFactory = acmeCertificateFactory;
            _selector = selector;
            _certificateRepositories = certificateRepositories;
            _domains = domains;
        }

        public override async Task<IAcmeState> MoveNextAsync(CancellationToken cancellationToken)
        {
            var domainSets = await _domains.GetDomainsAsync(cancellationToken);

            var account = await _acmeCertificateFactory.GetOrCreateAccountAsync(cancellationToken);
            _logger.LogInformation("Using account {accountId}", account.Id);

            var saveTasks = new List<Task>();

            foreach (var domainNames in domainSets)
            {
                try
                {
                    _logger.LogInformation("Creating certificate for {hostname}",
                        string.Join(",", domainNames));

                    var cert = await _acmeCertificateFactory.CreateCertificateAsync(domainNames, cancellationToken);

                    _logger.LogInformation("Created certificate {subjectName} ({thumbprint})",
                        cert.Subject,
                        cert.Thumbprint);

                    saveTasks.Add(SaveCertificateAsync(cert, cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(0, ex, "Failed to automatically create a certificate for {hostname}", domainNames);
                    throw;
                }
            }

            await Task.WhenAll(saveTasks);

            return MoveTo<CheckForRenewalState>();
        }

        private async Task SaveCertificateAsync(X509Certificate2 cert, CancellationToken cancellationToken)
        {
            _selector.Add(cert);

            var saveTasks = new List<Task>
            {
                Task.Delay(TimeSpan.FromMinutes(5), cancellationToken)
            };

            var errors = new List<Exception>();
            foreach (var repo in _certificateRepositories)
            {
                try
                {
                    saveTasks.Add(repo.SaveAsync(cert, cancellationToken));
                }
                catch (Exception ex)
                {
                    // synchronous saves may fail immediately
                    errors.Add(ex);
                }
            }

            await Task.WhenAll(saveTasks);

            if (errors.Count > 0)
            {
                throw new AggregateException("Failed to save cert to repositories", errors);
            }
        }
    }
}
