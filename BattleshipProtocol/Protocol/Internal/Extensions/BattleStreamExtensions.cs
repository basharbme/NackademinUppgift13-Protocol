﻿using System;
using System.Threading;
using System.Threading.Tasks;
using BattleshipProtocol.Protocol.Exceptions;
using JetBrains.Annotations;

namespace BattleshipProtocol.Protocol.Internal.Extensions
{
    internal static class BattleStreamExtensions
    {
        [NotNull]
        public static async Task EnsureVersionGreeting(this PacketConnection stream, string version, int timeout = Timeout.Infinite)
        {
            Response response = await EnsureResponse(stream, ResponseCode.VersionGreeting, timeout);

            if (string.IsNullOrWhiteSpace(response.Message))
            {
                throw new ProtocolException(ResponseCode.SyntaxError,
                    $"Missing version. Expected {version}.");
            }

            if (!response.Message.Equals(version, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ProtocolException(ResponseCode.SyntaxError,
                    $"Expected {version}, got {response.Message}");
            }
        }

        [NotNull]
        public static async Task<Response> EnsureResponse(this PacketConnection stream, ResponseCode code, int timeout = Timeout.Infinite)
        {
            Response response;

            try
            {
                response = await stream.ExpectResponseAsync(timeout);
            }
            catch (ProtocolException error)
            {
                throw new ProtocolException(error.ErrorCode,
                    $"{error.ErrorMessage} Expected {(short)code}.", error);
            }

            if (response.Code != code)
                throw new ProtocolException(ResponseCode.SequenceError,
                    $"Expected {(short)code}, got {(short)response.Code}.");

            return response;
        }
    }
}