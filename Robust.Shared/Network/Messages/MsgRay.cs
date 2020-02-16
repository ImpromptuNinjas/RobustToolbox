﻿using Lidgren.Network;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Maths;

namespace Robust.Shared.Network.Messages
{
    public class MsgRay : NetMessage
    {
        #region REQUIRED

        public const MsgGroups GROUP = MsgGroups.Command;
        public const string NAME = nameof(MsgRay);

        public MsgRay(INetChannel channel) : base(NAME, GROUP)
        {
        }

        #endregion

        public Vector2 RayOrigin { get; set; }
        public Vector2 RayHit { get; set; }
        public bool DidHit { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer, bool isCompressed = false)
        {
            DidHit = buffer.ReadBoolean();
            RayOrigin = buffer.ReadVector2();
            RayHit = buffer.ReadVector2();
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, bool useCompression = false)
        {
            buffer.Write(DidHit);
            buffer.Write(RayOrigin);
            buffer.Write(RayHit);
        }
    }
}
