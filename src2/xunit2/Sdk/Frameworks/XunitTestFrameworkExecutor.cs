using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit.Abstractions;

namespace Xunit.Sdk
{
    /// <summary>
    /// The implementation of <see cref="ITestFrameworkExecutor"/> that supports execution
    /// of unit tests linked against xunit2.dll.
    /// </summary>
    public class XunitTestFrameworkExecutor : LongLivedMarshalByRefObject, ITestFrameworkExecutor
    {
        readonly string assemblyFileName;
        readonly IAssemblyInfo assemblyInfo;

        /// <summary>
        /// Initializes a new instance of the <see cref="XunitTestFrameworkExecutor"/> class.
        /// </summary>
        /// <param name="assemblyFileName">Path of the test assembly.</param>
        public XunitTestFrameworkExecutor(string assemblyFileName)
        {
            this.assemblyFileName = assemblyFileName;

            var assembly = Assembly.Load(AssemblyName.GetAssemblyName(assemblyFileName));
            assemblyInfo = Reflector.Wrap(assembly);
        }

        static void CreateFixture(Type interfaceType, ExceptionAggregator aggregator, Dictionary<Type, object> mappings)
        {
            var fixtureType = interfaceType.GetGenericArguments().Single();
            aggregator.Run(() => mappings[fixtureType] = Activator.CreateInstance(fixtureType));
        }

        /// <inheritdoc/>
        public ITestCase Deserialize(string value)
        {
            return SerializationHelper.Deserialize<ITestCase>(value);
        }

        /// <inheritdoc/>
        public void Dispose() { }

        /// <inheritdoc/>
        public void Run(IEnumerable<ITestCase> testMethods, IMessageSink messageSink)
        {
            bool cancelled = false;
            var totalSummary = new RunSummary();

            string currentDirectory = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(assemblyInfo.AssemblyPath));

                if (messageSink.OnMessage(new TestAssemblyStarting
                    {
                        AssemblyFileName = assemblyFileName,
                        ConfigFileName = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile,
                        StartTime = DateTime.Now,
                        TestEnvironment = String.Format("{0}-bit .NET {1}", IntPtr.Size * 8, Environment.Version),
                        TestFrameworkDisplayName = XunitTestFrameworkDiscoverer.DisplayName
                    }))
                {
                    foreach (var collectionGroup in testMethods.Cast<XunitTestCase>().GroupBy(tc => tc.TestCollection))
                        cancelled = RunTestCollection(messageSink, collectionGroup.Key, collectionGroup, totalSummary, cancelled);
                }

                messageSink.OnMessage(new TestAssemblyFinished
                {
                    Assembly = assemblyInfo,
                    ExecutionTime = totalSummary.Time,
                    TestsRun = totalSummary.Total,
                    TestsFailed = totalSummary.Failed,
                    TestsSkipped = totalSummary.Skipped
                });
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDirectory);
            }
        }

        private bool RunTestCollection(IMessageSink messageSink, ITestCollection collection, IEnumerable<XunitTestCase> testCases, RunSummary totalSummary, bool cancelled)
        {
            var collectionSummary = new RunSummary();
            var collectionFixtureMappings = new Dictionary<Type, object>();
            var aggregator = new ExceptionAggregator();

            if (collection.CollectionDefinition != null)
            {
                var declarationType = ((IReflectionTypeInfo)collection.CollectionDefinition).Type;
                foreach (var interfaceType in declarationType.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
                    CreateFixture(interfaceType, aggregator, collectionFixtureMappings);
            }

            if (messageSink.OnMessage(new TestCollectionStarting { TestCollection = collection }))
            {
                foreach (var testCasesByClass in testCases.GroupBy(tc => tc.Class))
                {
                    var classSummary = new RunSummary();

                    if (!messageSink.OnMessage(new TestClassStarting { ClassName = testCasesByClass.Key.Name }))
                        cancelled = true;
                    else
                    {
                        cancelled = RunTestClass(messageSink, collection, collectionFixtureMappings, (IReflectionTypeInfo)testCasesByClass.Key, testCasesByClass, classSummary, aggregator);
                        collectionSummary.Aggregate(classSummary);
                    }

                    if (!messageSink.OnMessage(new TestClassFinished
                    {
                        Assembly = assemblyInfo,
                        ClassName = testCasesByClass.Key.Name,
                        ExecutionTime = classSummary.Time,
                        TestsFailed = classSummary.Failed,
                        TestsRun = classSummary.Total,
                        TestsSkipped = classSummary.Skipped
                    }))
                        cancelled = true;

                    if (cancelled)
                        break;
                }
            }

            foreach (var fixture in collectionFixtureMappings.Values.OfType<IDisposable>())
            {
                try
                {
                    fixture.Dispose();
                }
                catch (Exception ex)
                {
                    if (!messageSink.OnMessage(new ErrorMessage(ex.Unwrap())))
                        cancelled = true;
                }
            }

            messageSink.OnMessage(new TestCollectionFinished
            {
                Assembly = assemblyInfo,
                ExecutionTime = collectionSummary.Time,
                TestCollection = collection,
                TestsFailed = collectionSummary.Failed,
                TestsRun = collectionSummary.Total,
                TestsSkipped = collectionSummary.Skipped
            });

            totalSummary.Aggregate(collectionSummary);
            return cancelled;
        }

        private static bool RunTestClass(IMessageSink messageSink,
                                         ITestCollection collection,
                                         Dictionary<Type, object> collectionFixtureMappings,
                                         IReflectionTypeInfo testClass,
                                         IEnumerable<XunitTestCase> testCases,
                                         RunSummary classSummary,
                                         ExceptionAggregator aggregator)
        {
            var cancelled = false;
            var testClassType = testClass.Type;
            var fixtureMappings = new Dictionary<Type, object>();
            var constructorArguments = new List<object>();

            if (testClassType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)))
                aggregator.Add(new TestClassException("A test class may not be decorated with ICollectionFixture<> (decorate the test collection class instead)."));

            foreach (var interfaceType in testClassType.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>)))
                CreateFixture(interfaceType, aggregator, fixtureMappings);

            if (collection.CollectionDefinition != null)
            {
                var declarationType = ((IReflectionTypeInfo)collection.CollectionDefinition).Type;
                foreach (var interfaceType in declarationType.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IClassFixture<>)))
                    CreateFixture(interfaceType, aggregator, fixtureMappings);
            }

            var isStaticClass = testClassType.IsAbstract && testClassType.IsSealed;
            if (!isStaticClass)
            {
                var ctors = testClassType.GetConstructors();
                if (ctors.Length != 1)
                {
                    aggregator.Add(new TestClassException("A test class may only define a single public constructor."));
                }
                else
                {
                    var ctor = ctors.Single();
                    List<string> unusedArguments = new List<string>();

                    foreach (var paramInfo in ctor.GetParameters())
                    {
                        object fixture;

                        if (fixtureMappings.TryGetValue(paramInfo.ParameterType, out fixture) || collectionFixtureMappings.TryGetValue(paramInfo.ParameterType, out fixture))
                            constructorArguments.Add(fixture);
                        else
                            unusedArguments.Add(paramInfo.ParameterType.Name + " " + paramInfo.Name);
                    }

                    if (unusedArguments.Count > 0)
                        aggregator.Add(new TestClassException("The following constructor arguments did not have matching fixture data: " + String.Join(", ", unusedArguments)));
                }
            }

            var methodGroups = testCases.GroupBy(tc => tc.Method);

            foreach (var method in methodGroups)
            {
                if (!messageSink.OnMessage(new TestMethodStarting { ClassName = testClass.Name, MethodName = method.Key.Name }))
                    cancelled = true;
                else
                    cancelled = RunTestMethod(messageSink, constructorArguments.ToArray(), method, classSummary, aggregator);

                if (!messageSink.OnMessage(new TestMethodFinished { ClassName = testClass.Name, MethodName = method.Key.Name }))
                    cancelled = true;

                if (cancelled)
                    break;
            }

            foreach (var fixture in fixtureMappings.Values.OfType<IDisposable>())
            {
                try
                {
                    fixture.Dispose();
                }
                catch (Exception ex)
                {
                    if (!messageSink.OnMessage(new ErrorMessage(ex.Unwrap())))
                        cancelled = true;
                }
            }

            return cancelled;
        }

        private static bool RunTestMethod(IMessageSink messageSink, object[] constructorArguments, IEnumerable<XunitTestCase> testCases, RunSummary classSummary, ExceptionAggregator aggregator)
        {
            bool cancelled = false;

            foreach (XunitTestCase testCase in testCases)
            {
                var delegatingSink = new DelegatingMessageSink<ITestCaseFinished>(messageSink);

                // REVIEW: testCase.Run() returning bool implies synchronous behavior, which will probably
                // not be true once we start supporting parallelization. This could be achieved by always
                // using a delegating sink (like above) and watching for cancellation there, then checking
                // for the cancellation result in the delegating sink after work is finished.

                cancelled = testCase.Run(delegatingSink, constructorArguments, aggregator);
                delegatingSink.Finished.WaitOne();

                classSummary.Total += delegatingSink.FinalMessage.TestsRun;
                classSummary.Failed += delegatingSink.FinalMessage.TestsFailed;
                classSummary.Skipped += delegatingSink.FinalMessage.TestsSkipped;
                classSummary.Time += delegatingSink.FinalMessage.ExecutionTime;

                if (cancelled)
                    break;
            }

            return cancelled;
        }

        class RunSummary
        {
            public int Total = 0;
            public int Failed = 0;
            public int Skipped = 0;
            public decimal Time = 0M;

            public void Aggregate(RunSummary other)
            {
                Total += other.Total;
                Failed += other.Failed;
                Skipped += other.Skipped;
                Time += other.Time;
            }
        }
    }
}