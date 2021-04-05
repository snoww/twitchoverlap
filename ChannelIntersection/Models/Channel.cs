﻿using System;
using System.Collections.Generic;

#nullable disable

namespace ChannelIntersection.Models
{
    public class Channel
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Avatar { get; set; }
        public string Game { get; set; }
        public int Viewers { get; set; }
        public int Chatters { get; set; }
        public int Shared { get; set; }
        public DateTime LastUpdate { get; set; }

        public virtual ICollection<Overlap> Histories { get; set; } = new List<Overlap>();

        public Channel(string id, string game, int viewers, int chatters, int shared, DateTime lastUpdate, string avatar, string displayName)
        {
            Id = id;
            Game = game;
            Viewers = viewers;
            Chatters = chatters;
            Shared = shared;
            LastUpdate = lastUpdate;
            Avatar = avatar;
            DisplayName = displayName;
        }
    }
}