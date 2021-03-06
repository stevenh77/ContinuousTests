﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoTest.Core.Messaging.MessageConsumers;
using AutoTest.Messages;
using AutoTest.VM.Messages;
using AutoTest.Core.BuildRunners;
using AutoTest.Core.TestRunners;
using AutoTest.TestRunners.Shared;
using AutoTest.TestRunners.Shared.Plugins;
using AutoTest.Core.Caching.Projects;
using AutoTest.Core.Caching;
using AutoTest.Core.Configuration;
using System.IO;

namespace AutoTest.VM
{
    class OnDemandRunInternal : OnDemandRun
    {
        public TestRunner Runner { get; private set; }

        public OnDemandRunInternal(TestRunner runner, string project, string[] tests, string[] members, string[] namespaces)
            : base(project, tests, members, namespaces)
        {
            Runner = runner;
        }
    }

    class OnDemandRunAppender
    {
        private List<OnDemandRunInternal> _list;
        private IEnumerable<IAutoTestNetTestRunner> _identifiers;
        private string _assembly;

        public OnDemandRunAppender(List<OnDemandRunInternal> list, IEnumerable<IAutoTestNetTestRunner> identifiers, string assembly)
        {
            _list = list;
            _identifiers = identifiers;
            _assembly = assembly;
        }

        public void Append(OnDemandRun run)
        {
            foreach (var runner in _identifiers)
            {
                var members = new List<string>();
                var namespaces = new List<string>();
                var tests = getTests(run, runner);
                if (tests.Count() == 0)
                    members.AddRange(getMemebers(run, runner));
                if (tests.Count() == 0 && members.Count == 0)
                    namespaces.AddRange(run.Namespaces);
                if (nothingToTest(run, tests, members, namespaces))
                {
                    Logger.WriteDebug("Nothing to test for on demand run. Skipping " + run.Project + " for runner " + runner.Identifier);
                    addEmtpyRun(run, runner);
                    continue;
                }
                var newRun = add(run, runner, tests, members, namespaces);
                if (newRun == null)
                    continue;
                if (run.RunAllTestsInProject)
                    newRun.ShouldRunAllTestsInProject();
            }
        }

        private void addEmtpyRun(OnDemandRun run, IAutoTestNetTestRunner runner)
        {
            _list.Add(new OnDemandRunInternal(TestRunnerConverter.FromString(runner.Identifier), run.Project, new string[] {}, new string[] {}, new string[] {}));
        }

        private OnDemandRunInternal add(OnDemandRun run, IAutoTestNetTestRunner runner, IEnumerable<string> tests, IEnumerable<string> members, IEnumerable<string> namespaces)
        {
            if (alreadySetToTestAll(run))
                return null;
            var existing = _list.Where(x => x.Project.Equals(run.Project) && x.Runner.Equals(TestRunnerConverter.FromString(runner.Identifier))).FirstOrDefault();
            if (existing != null)
            {
                Logger.WriteDebug("Joining with existing run " + existing.Project + " with runner " + runner.Identifier);
                existing.JoinWith(tests, members, namespaces);
                return existing;
            }
            Logger.WriteDebug("Adding new run " + run.Project + " with runner " + runner.Identifier);
            var internalRun = new OnDemandRunInternal(TestRunnerConverter.FromString(runner.Identifier), run.Project, tests.ToArray(), members.ToArray(), namespaces.ToArray());
            _list.Add(internalRun);
            return _list[_list.Count - 1];
        }

        private bool alreadySetToTestAll(OnDemandRun run)
        {
            return run.RunAllTestsInProject && _list.Where(x => x.Project.Equals(run.Project)).Count() > 0;
        }

        private bool nothingToTest(OnDemandRun run, IEnumerable<string> tests, IEnumerable<string> members, IEnumerable<string> namespaces)
        {
            return namespaces.Count() == 0 && members.Count() == 0 && tests.Count() == 0 && !run.RunAllTestsInProject;
        }

        private IEnumerable<string> getMemebers(OnDemandRun run, IAutoTestNetTestRunner runner)
        {
            var members = new List<string>();
            foreach (var member in run.Members)
            {
                Logger.WriteDebug(string.Format("Checking {0} using {1} for memeber {2}", _assembly, runner.Identifier, member));
                if (runner.ContainsTestsFor(_assembly, member))
                {
                    Logger.WriteDebug(string.Format("{0} contains {1} tests", member, runner.Identifier));
                    members.Add(member);
                }
            }
            return members;
        }

        private IEnumerable<string> getTests(OnDemandRun run, IAutoTestNetTestRunner runner)
        {
            var tests = new List<string>();
            foreach (var test in run.Tests)
            {
                Logger.WriteDebug(string.Format("Checking {0} using {1} for test {2}", _assembly, runner.Identifier, test));
                if (runner.IsTest(_assembly, test))
                {
                    Logger.WriteDebug(string.Format("{0} is a {1} test", test, runner.Identifier));
                    tests.Add(test);
                }
            }
            return tests;
        }
    }

    class OnDemanTestrunPreprocessor : IPreProcessBuildruns, IPreProcessTestruns
    {
        private ICache _cache;
        private IConfiguration _configuration;
        private bool _isActive = false;
        private List<OnDemandRunInternal> _addToNextRun = new List<OnDemandRunInternal>();
        private IEnumerable<IAutoTestNetTestRunner> _identifiers = null;

        public OnDemanTestrunPreprocessor(ICache cache, IConfiguration configuration)
        {
            _cache = cache;
            _configuration = configuration;
        }

        public void Activate()
        {
            _isActive = true;
        }

        public void Deactivate()
        {
            _isActive = false;
        }

        public void AddRuns(OnDemandRun run)
        {
            Logger.WriteDebug("About to add add run for " + run.Project);
            var assembly = getAssembly(run);
            if (assembly == null)
                return;
            var appender = new OnDemandRunAppender(_addToNextRun, getIdentifiers(), assembly);
            appender.Append(run);
        }

        private string getAssembly(OnDemandRun run)
        {
            var projects = _cache.GetAll<Project>();
            var project = projects.Where(x => x.Key.Equals(run.Project)).FirstOrDefault();
            if (project == null)
            {
                Logger.WriteDebug("Could not match project for on demand run " + run.Project);
                return null;
            }
            var assembly = project.GetAssembly(_configuration.CustomOutputPath);
            if (!File.Exists(assembly))
            {
                Logger.WriteError(string.Format("Assembly {0} does not exist and cannot be tested", assembly));
                return null;
            }
            else
                Logger.WriteDebug(string.Format("Bound test run to assembly {0}", assembly));
            return assembly;
        }

        private IEnumerable<IAutoTestNetTestRunner> getIdentifiers()
        {
            if (_identifiers == null)
            {
                var locator = new PluginLocator(Path.GetFullPath("TestRunners"));
                var plugins = locator.Locate();
                _identifiers = plugins
                    .Select(x => 
                            {
                                var instance = x.New();
                                if (instance == null)
                                    Logger.WriteDebug(string.Format("Could not create plugin for {0} using {1}", x.Type, x.Assembly));
                                return instance;
                            })
                    .Where(x => x != null);
            }
            return _identifiers;
        }

        RunInfo[] IPreProcessBuildruns.PreProcess(RunInfo[] details)
        {
            if (!_isActive)
                return details;
            foreach (var detail in details)
                detail.ShouldNotBuild();
            return details;
        }

        BuildRunResults IPreProcessBuildruns.PostProcessBuildResults(BuildRunResults buildResults)
        {
            return buildResults;
        }

        RunInfo[] IPreProcessBuildruns.PostProcess(RunInfo[] details, ref RunReport runReport)
        {
            return details;
        }

        PreProcessedTesRuns IPreProcessTestruns.PreProcess(PreProcessedTesRuns preProcessed)
        {
            if (!_isActive)
                return preProcessed;
            var runDetails = new List<RunInfo>();
            foreach (var run in _addToNextRun)
                Logger.WriteDebug("On demand run for preprocessing " + run.Project);
            foreach (var detail in preProcessed.RunInfos)
            {
                var project = "Invalid project";
                if (detail.Project != null)
                    project = detail.Project.Key;
                Logger.WriteDebug(string.Format("On demand preprocessor handling {0} ({1})", detail.Assembly, project));
                var runs = _addToNextRun.Where(x => detail.Project != null && x.Project.Equals(detail.Project.Key));
                foreach (var run in runs)
                {
                    Logger.WriteDebug(string.Format("Detail matched to run {0} for runner {1}", run.Project, run.Runner));
                    if (!run.RunAllTestsInProject)
                    {
                        Logger.WriteDebug(string.Format("Should run only specified tests for {0}", run.Runner));
                        detail.ShouldOnlyRunSpcifiedTestsFor(run.Runner);
                        var tests = getList(run.Runner, run.Tests);
                        Logger.WriteDebug(string.Format("Containins {0} tests", tests.Length));
                        detail.AddTestsToRun(tests);
                        var members = getList(run.Runner, run.Members);
                        Logger.WriteDebug(string.Format("Containins {0} members", members.Length));
                        detail.AddMembersToRun(members);
                        var namespaces = getList(run.Runner, run.Namespaces);
                        Logger.WriteDebug(string.Format("Containins {0} namespaces", namespaces.Length));
                        detail.AddNamespacesToRun(namespaces);
                    }
                    else
                    {
                        Logger.WriteDebug(string.Format("Should run all tests for {0}", run.Runner));
                    }
                }
                if (runs.Count() > 0)
                    runDetails.Add(detail);
            }
            _addToNextRun.Clear();
            return new PreProcessedTesRuns(preProcessed.ProcessWrapper, runDetails.ToArray());
        }

        void IPreProcessTestruns.RunFinished(TestRunResults[] results)
        {
        }

        private TestToRun[] getList(TestRunner testRunner, string[] list)
        {
            var result = new List<TestToRun>();
            foreach (var item in list)
                result.Add(new TestToRun(testRunner, item));
            return result.ToArray();
        }
    }
}
