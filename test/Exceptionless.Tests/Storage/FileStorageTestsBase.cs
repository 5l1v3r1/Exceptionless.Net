﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Extensions;
using Exceptionless.Models;
using Exceptionless.Serializer;
using Exceptionless.Storage;
using Xunit;

namespace Exceptionless.Tests.Storage {
    public abstract class FileStorageTestsBase {
        protected abstract IObjectStorage GetStorage();

        [Fact]
        public void CanManageFiles() {
            Reset();

            var storage = GetStorage();
            storage.SaveObject("test.txt", "test");
            Assert.Single(storage.GetObjectList("test.txt"));
            Assert.Single(storage.GetObjectList());
            var file = storage.GetObjectList().FirstOrDefault();
            Assert.NotNull(file);
            Assert.Equal("test.txt", file.Path);
            string content = storage.GetObject<string>("test.txt");
            Assert.Equal("test", content);
            storage.RenameObject("test.txt", "new.txt");
            Assert.Contains(storage.GetObjectList(), f => f.Path == "new.txt");
            storage.DeleteObject("new.txt");
            Assert.Empty(storage.GetObjectList());
            storage.SaveObject(Path.Combine("test", "q", Guid.NewGuid().ToString("N") + ".txt"), "test");
            Assert.Single(storage.GetObjectList(Path.Combine("test","q", "*.txt")));
            Assert.Single(storage.GetObjectList("*", null, DateTime.Now));
            var files = storage.GetObjectList("*", null, DateTime.Now.Subtract(TimeSpan.FromMinutes(5))).ToList();
            Debug.WriteLine(String.Join(",", files.Select(f => f.Path + " " + f.Created)));
            Assert.Empty(files);
        }

        [Fact]
        public void CanManageQueue() {
            Reset();

            var storage = GetStorage();
            const string queueName = "test";

            IJsonSerializer serializer = new DefaultJsonSerializer();
            var ev = new Event { Type = Event.KnownTypes.Log, Message = "test" };
            storage.Enqueue(queueName, ev);
            storage.SaveObject("test.txt", "test");
            Assert.Contains(storage.GetObjectList(), f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("0.json"));
            Assert.Equal(2, storage.GetObjectList().Count());

            Assert.True(storage.GetQueueFiles(queueName).All(f => f.Path.EndsWith("0.json")));
            Assert.Single(storage.GetQueueFiles(queueName));

            storage.DeleteObject("test.txt");
            Assert.Single(storage.GetObjectList());

            Assert.True(storage.LockFile(storage.GetObjectList().FirstOrDefault()));
            Assert.True(storage.GetQueueFiles(queueName).All(f => f.Path.EndsWith("0.json.x")));
            Assert.True(storage.ReleaseFile(storage.GetObjectList().FirstOrDefault()));

            var batch = storage.GetEventBatch(queueName, serializer);
            Assert.Equal(1, batch.Count);

            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("1.json.x")));
            Assert.Single(storage.GetObjectList());

            Assert.Empty(storage.GetQueueFiles(queueName));
            Assert.Empty(storage.GetEventBatch(queueName, serializer));

            Assert.False(storage.LockFile(storage.GetObjectList().FirstOrDefault()));

            storage.ReleaseBatch(batch);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("1.json")));
            Assert.Single(storage.GetObjectList());
            Assert.Single(storage.GetQueueFiles(queueName));

            var file = storage.GetObjectList().FirstOrDefault();
            storage.IncrementAttempts(file);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("2.json")));
            storage.IncrementAttempts(file);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("3.json")));

            Assert.True(storage.LockFile(file));
            Assert.NotNull(file);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("3.json.x")));
            Thread.Sleep(TimeSpan.FromMilliseconds(1));
            storage.ReleaseStaleLocks(queueName, TimeSpan.Zero);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("3.json")));

            batch = storage.GetEventBatch(queueName, serializer);
            Assert.Equal(1, batch.Count);
            Assert.True(storage.GetObjectList().All(f => f.Path.StartsWith(Path.Combine(queueName, "q")) && f.Path.EndsWith("4.json.x")));
            storage.DeleteBatch(batch);
            Assert.Empty(storage.GetQueueFiles(queueName));

            ev = new Event { Type = Event.KnownTypes.Log, Message = "test" };
            storage.Enqueue(queueName, ev);
            file = storage.GetObjectList().FirstOrDefault();
            Assert.NotNull(file);
            Thread.Sleep(TimeSpan.FromMilliseconds(1));
            storage.CleanupQueueFiles(queueName, TimeSpan.Zero);
            Assert.Empty(storage.GetQueueFiles(queueName));
        }

        private void Reset() {
            var storage = GetStorage();
            var files = storage.GetObjectList();
            if (files.Any())
                Debug.WriteLine("Got files");
            else
                Debug.WriteLine("No files");
            storage.DeleteFiles(storage.GetObjectList());
            Assert.Empty(storage.GetObjectList());
        }

        [Fact]
        public void CanConcurrentlyManageFiles() {
            Reset();

            var storage = GetStorage();
            IJsonSerializer serializer = new DefaultJsonSerializer();
            const string queueName = "test";

            Parallel.For(0, 25, i => {
                var ev = new Event {
                    Type = Event.KnownTypes.Log,
                    Message = "test" + i
                };
                storage.Enqueue(queueName, ev);
            });
            Assert.Equal(25, storage.GetObjectList().Count());
            var working = new ConcurrentDictionary<string, object>();

            Parallel.For(0, 50, i => {
                var fileBatch = storage.GetEventBatch(queueName, serializer, 2);
                foreach (var f in fileBatch) {
                    if (working.ContainsKey(f.Item1.Path))
                        Debug.WriteLine(f.Item1.Path);
                    Assert.False(working.ContainsKey(f.Item1.Path));
                    working.TryAdd(f.Item1.Path, null);
                }

                if (RandomData.GetBool()) {
                    foreach (var f in fileBatch)
                        working.TryRemove(f.Item1.Path, out _);
                    storage.ReleaseBatch(fileBatch);
                } else {
                    storage.DeleteBatch(fileBatch);
                }
            });
            Assert.Equal(25, working.Count + storage.GetQueueFiles(queueName).Count);
        }
    }
}
