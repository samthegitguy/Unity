﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace GitHub.Unity
{
    class ApplicationManagerBase : IApplicationManager
    {
        protected static readonly ILogging logger = Logging.GetLogger<IApplicationManager>();

        private RepositoryLocator repositoryLocator;
        private RepositoryManager repositoryManager;

        public ApplicationManagerBase(SynchronizationContext synchronizationContext)
        {
            SynchronizationContext = synchronizationContext;
            SynchronizationContext.SetSynchronizationContext(SynchronizationContext);
            ThreadingHelper.SetMainThread();
            UIScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            ThreadingHelper.MainThreadScheduler = UIScheduler;
            CancellationTokenSource = new CancellationTokenSource();
        }

        protected void Initialize(IUIDispatcher uiDispatcher)
        {
            // accessing Environment triggers environment initialization if it hasn't happened yet
            LocalSettings = new LocalSettings(Environment);
            UserSettings = new UserSettings(Environment, ApplicationInfo.ApplicationName);
            SystemSettings = new SystemSettings(Environment, ApplicationInfo.ApplicationName);

            Platform = new Platform(Environment, FileSystem, uiDispatcher);
            ProcessManager = new ProcessManager(Environment, Platform.GitEnvironment, CancellationToken);
            Platform.Initialize(ProcessManager);
            ApiClientFactory.Instance = new ApiClientFactory(new AppConfiguration(), Platform.CredentialManager);
        }

        public virtual Task Run()
        {
            Task task = null;
            try
            {
                task = RunInternal();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                throw;
            }
            return task;
        }

        protected virtual void InitializeEnvironment()
        {
            SetupRepository();
        }

        private void SetupRepository()
        {
            if (FileSystem == null)
            {
                FileSystem = new FileSystem();
                NPathFileSystemProvider.Current = FileSystem;
            }

            FileSystem.SetCurrentDirectory(Environment.UnityProjectPath);

            // figure out where the repository root is
            repositoryLocator = new RepositoryLocator(Environment.UnityProjectPath);
            var repositoryPath = repositoryLocator.FindRepositoryRoot();
            if (repositoryPath != null)
            {
                // Make sure CurrentDirectory always returns the repository root, so all
                // file system path calculations use it as a base
                FileSystem.SetCurrentDirectory(repositoryPath);
            }
        }

        public virtual async Task RestartRepository()
        {
            await ThreadingHelper.SwitchToThreadAsync();

            SetupRepository();

            var repositoryRoot = repositoryLocator.FindRepositoryRoot();
            if (repositoryRoot != null)
            {
                try
                {
                    var repositoryManagerFactory = new RepositoryManagerFactory();
                    repositoryManager = repositoryManagerFactory.CreateRepositoryManager(Platform, TaskRunner, repositoryRoot, CancellationToken);
                }
                catch (Exception ex)
                {
                    logger.Error(ex);
                }
                Environment.Repository = repositoryManager.Repository;
                repositoryManager.Initialize();
                repositoryManager.Start();
            }
        }

        private async Task RunInternal()
        {
            await ThreadingHelper.SwitchToThreadAsync();

            var gitSetup = new GitSetup(Environment, FileSystem, CancellationToken);
            var expectedPath = gitSetup.GitInstallationPath;

            var setupDone = await gitSetup.SetupIfNeeded(
                //new Progress<float>(x => logger.Trace("Percentage: {0}", x)),
                //new Progress<long>(x => logger.Trace("Remaining: {0}", x))
            );

            if (setupDone)
                Environment.GitExecutablePath = gitSetup.GitExecutablePath;
            else
                Environment.GitExecutablePath = await LookForGitInstallationPath();

            logger.Trace("Environment.GitExecutablePath \"{0}\" Exists:{1}", gitSetup.GitExecutablePath, gitSetup.GitExecutablePath.FileExists());

            await RestartRepository();

            if (Environment.IsWindows)
            {
                string credentialHelper = null;
                var gitConfigGetTask = new GitConfigGetTask(Environment, ProcessManager,
                    new TaskResultDispatcher<string>(s => {
                        credentialHelper = s;
                    }), "credential.helper", GitConfigSource.Global);


                await gitConfigGetTask.RunAsync(CancellationToken.None);

                if (credentialHelper != "wincred")
                {
                    var gitConfigSetTask = new GitConfigSetTask(Environment, ProcessManager,
                        new TaskResultDispatcher<string>(s => { }), "credential.helper", "wincred",
                        GitConfigSource.Global);

                    await gitConfigSetTask.RunAsync(CancellationToken.None);
                }
            }
        }

        private async Task<string> LookForGitInstallationPath()
        {
            NPath cachedGitInstallPath = null;
            var path = SystemSettings.Get("GitInstallPath");
            if (!String.IsNullOrEmpty(path))
                cachedGitInstallPath = path.ToNPath();

            // Root paths
            if (cachedGitInstallPath == null ||
               !cachedGitInstallPath.DirectoryExists())
            {
                return await GitEnvironment.FindGitInstallationPath(ProcessManager);
            }
            else
            {
                return cachedGitInstallPath.ToString();
            }
        }

        private bool disposed = false;
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (disposed) return;
                disposed = true;
                CancellationTokenSource.Cancel();
                repositoryManager.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual IEnvironment Environment { get; set; }
        public IFileSystem FileSystem { get; protected set; }
        public IPlatform Platform { get; protected set; }
        public virtual IProcessEnvironment GitEnvironment { get; set; }
        public IProcessManager ProcessManager { get; protected set; }
        public ITaskResultDispatcher MainThreadResultDispatcher { get; protected set; }
        public CancellationToken CancellationToken { get; protected set; }
        public ITaskRunner TaskRunner { get; protected set; }

        protected CancellationTokenSource CancellationTokenSource { get; private set; }
        protected TaskScheduler UIScheduler { get; private set; }
        protected SynchronizationContext SynchronizationContext { get; private set; }
        protected IRepositoryManager RepositoryManager { get { return repositoryManager; } }

        public ISettings LocalSettings { get; protected set; }
        public ISettings SystemSettings { get; protected set; }
        public ISettings UserSettings { get; protected set; }
    }
}
