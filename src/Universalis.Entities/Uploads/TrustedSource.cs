﻿using MongoDB.Bson.Serialization.Attributes;

namespace Universalis.Entities.Uploads
{
    public class TrustedSource
    {
        [BsonElement("sourceName")]
        public string Name { get; init; }

        [BsonElement("apiKey")]
        public string ApiKeySha256 { get; init; } // There's no real reason for this to be hashed, but ~legacy~

        [BsonElement("uploadCount")]
        public uint UploadCount { get; init; }
    }
}