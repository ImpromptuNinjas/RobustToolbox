﻿using System;
using Lidgren.Network;
using Robust.Shared.GameStates;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.IoC;
using System.IO;
using System.IO.Compression;
using Robust.Shared.Utility;

#nullable disable

namespace Robust.Shared.Network.Messages
{

    public class MsgState : NetMessage
    {

        // If a state is large enough we send it ReliableUnordered instead.
        // This is to avoid states being so large that they consistently fail to reach the other end
        // (due to being in many parts).
        public const int ReliableThreshold = 1300;

        #region REQUIRED

        public static readonly MsgGroups GROUP = MsgGroups.Entity;

        public static readonly string NAME = nameof(MsgState);

        public MsgState(INetChannel channel) : base(NAME, GROUP)
        {
        }

        #endregion

        public GameState State { get; set; }

        private bool _hasWritten;

        public override void ReadFromBuffer(NetIncomingMessage buffer)
        {
            MsgSize = buffer.LengthBytes;
            var length = buffer.ReadVariableInt32();
            if (length < 0)
            {
                length = -length;
            var stateData = buffer.ReadBytes(length);
                using var stateStream = new MemoryStream(stateData);
                var serializer = IoCManager.Resolve<IRobustSerializer>();
                using var deflateStream = new DeflateStream(stateStream, CompressionMode.Decompress, true);
                using var bufferStream = new MemoryStream();
                deflateStream.CopyTo(bufferStream);
                bufferStream.Position = 0;
                State = serializer.Deserialize<GameState>(bufferStream);
            }
            else
            {
                var stateData = buffer.ReadBytes(length);
                using var stateStream = new MemoryStream(stateData);
                var serializer = IoCManager.Resolve<IRobustSerializer>();
                State = serializer.Deserialize<GameState>(stateStream);
            }

            State.PayloadSize = length;
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            using (var stateStream = new MemoryStream())
            {
                DebugTools.Assert(stateStream.Length <= Int32.MaxValue);

                using var bufferStream = new MemoryStream();
                serializer.Serialize(bufferStream, State);
                bufferStream.Position = 0;

                if (bufferStream.Length > 32)
                {
                    using (var deflateStream = new DeflateStream(stateStream, CompressionLevel.Optimal, true))
                    {
                        bufferStream.CopyTo(deflateStream);
                        bufferStream.Position = 0;
                    }

                    if (stateStream.Length > bufferStream.Length)
                    {
                        buffer.WriteVariableInt32((int) bufferStream.Length);
                        buffer.Write(bufferStream.ToArray());
                    }
                    else
                    {
                        buffer.WriteVariableInt32((int) -stateStream.Length);
                buffer.Write(stateStream.ToArray());
            }
                }
                else
                {
                    buffer.WriteVariableInt32((int) bufferStream.Length);
                    buffer.Write(bufferStream.ToArray());
                }
            }

            _hasWritten = false;
            MsgSize = buffer.LengthBytes;
        }

        /// <summary>
        ///     Whether this state message is large enough to warrant being sent reliably.
        ///     This is only valid after
        /// </summary>
        /// <returns></returns>
        public bool ShouldSendReliably()
        {
            // This check will be true in integration tests.
            // TODO: Maybe handle this better so that packet loss integration testing can be done?
            if (!_hasWritten)
            {
                return true;
            }

            return MsgSize > ReliableThreshold;
        }

        public override NetDeliveryMethod DeliveryMethod
        {
            get
            {
                if (ShouldSendReliably())
                {
                    return NetDeliveryMethod.ReliableUnordered;
                }

                return base.DeliveryMethod;
            }
        }

    }

}
