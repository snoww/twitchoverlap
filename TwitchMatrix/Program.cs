﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using ChannelIntersection.Models;
using MathNet.Numerics.LinearAlgebra;
using Npgsql;
using NpgsqlTypes;

namespace TwitchMatrix
{
    public class Program
    {
        private static readonly HttpClient Http = new();
        private static readonly Dictionary<string, HashSet<string>> Chatters = new();
        private static readonly string TwitchToken = Environment.GetEnvironmentVariable("TWITCH_TOKEN");
        private static readonly string TwitchClient = Environment.GetEnvironmentVariable("TWITCH_CLIENT");
        private static readonly string PsqlConn = Environment.GetEnvironmentVariable("PSQL_CONN");
        private static TwitchContext _context;

        private static readonly object WriteLock = new();


        public static async Task Main(string[] args)
        {
            
            Console.WriteLine("starting twitch matrix");
            await using var dbContext = new TwitchContext(@"Host=192.168.1.100;Database=twitch_overlap;Username=roki;Password=roki-snow;");
            _context = dbContext;
            // var r = new ReadDb(dbContext);
            // r.Read();
            // var k = new KComb(_context);
            // k.DoStuff();
            // return;
            
            var sw = new Stopwatch();
            sw.Start();

            Dictionary<string, Channel> topChannels = await GetTopChannels();
            Console.WriteLine($"Retrieved {topChannels.Count} channels in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();
            var channels = new ConcurrentBag<string>();
            // var channelChatters = new Dictionary<string, HashSet<string>>();

            IEnumerable<Task> processTasks = topChannels.Select(async channel =>
            {
                (string name, Channel ch) = channel;
                await GetChatters(ch);
                
                // foreach (string chatter in chChatters)
                // {
                //     Chatters[chatter].Add(name);
                // }

                channels.Add(name);
                // channelChatters.TryAdd(name, chChatters);
            });

            await Task.WhenAll(processTasks);

            Console.WriteLine($"Retrieved {Chatters.Count} chatters in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();
            
            // list of ALL unique chatters retrieved
            // string[] chatters = Chatters.Keys.ToArray();
            // list of ALL channels retrieved
            // string[] channels = channelChatters.Select(x => x.Key).ToArray();
            
            await using var conn = new NpgsqlConnection(PsqlConn);
            await conn.OpenAsync();
            
            await using (NpgsqlBinaryImporter writer = conn.BeginBinaryImport("COPY chatters2 (time, users, channels) FROM STDIN (FORMAT BINARY)"))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(DateTime.UtcNow, NpgsqlDbType.Timestamp);
                await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(Chatters), NpgsqlDbType.Jsonb);
                await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(channels), NpgsqlDbType.Jsonb);
                await writer.CompleteAsync();
            }
            
            Console.WriteLine($"Inserted into database in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();
        }

        private static async Task<Dictionary<string, Channel>> GetTopChannels()
        {
            var channels = new Dictionary<string, Channel>();
            var pageToken = string.Empty;
            do
            {
                using var request = new HttpRequestMessage();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchToken);
                request.Headers.Add("Client-Id", TwitchClient);
                request.RequestUri = string.IsNullOrWhiteSpace(pageToken)
                    ? new Uri("https://api.twitch.tv/helix/streams?first=100")
                    : new Uri($"https://api.twitch.tv/helix/streams?first=100&after={pageToken}");

                using HttpResponseMessage response = await Http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                    pageToken = json.RootElement.GetProperty("pagination").GetProperty("cursor").GetString();
                    JsonElement.ArrayEnumerator channelEnumerator = json.RootElement.GetProperty("data").EnumerateArray();
                    foreach (JsonElement channel in channelEnumerator)
                    {
                        int viewerCount = channel.GetProperty("viewer_count").GetInt32();
                        if (viewerCount < 1500)
                        {
                            pageToken = null;
                            break;
                        }

                        string login = channel.GetProperty("user_login").GetString()?.ToLowerInvariant();
                        var dbChannel = new Channel(login, channel.GetProperty("user_name").GetString(), channel.GetProperty("game_name").GetString(), viewerCount, DateTime.Now);

                        channels.TryAdd(login, dbChannel);
                    }
                }
            } while (pageToken != null);

            return channels;
        }


        private static async Task<HashSet<string>> GetChatters(Channel channel)
        {
            Stream stream;
            try
            {
                stream = await Http.GetStreamAsync($"https://tmi.twitch.tv/group/user/{channel.LoginName}/chatters");
            }
            catch
            {
                Console.WriteLine($"Could not retrieve chatters for {channel.LoginName}");
                return null;
            }

            using JsonDocument response = await JsonDocument.ParseAsync(stream);
            await stream.DisposeAsync();

            int chatters = response.RootElement.GetProperty("chatter_count").GetInt32();
            if (chatters < 500)
            {
                return null;
            }

            channel.Chatters = chatters;
            var chatterList = new HashSet<string>(chatters);
            JsonElement.ObjectEnumerator viewerTypes = response.RootElement.GetProperty("chatters").EnumerateObject();
            foreach (JsonProperty viewerType in viewerTypes)
            {
                foreach (JsonElement viewer in viewerType.Value.EnumerateArray())
                {
                    string username = viewer.GetString()?.ToLower();
                    if (username == null || username.EndsWith("bot", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // chatterList.Add(username);
                    lock (WriteLock)
                    {
                        if (!Chatters.ContainsKey(username))
                        {
                            Chatters.TryAdd(username, new HashSet<string>{channel.LoginName});
                        }
                        else
                        {
                            Chatters[username].Add(channel.LoginName);
                        }
                    }
                }
            }

            return chatterList;
        }
    }
}