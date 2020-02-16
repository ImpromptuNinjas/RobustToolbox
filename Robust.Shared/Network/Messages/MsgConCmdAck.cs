﻿using Lidgren.Network;
using Robust.Shared.Interfaces.Network;

namespace Robust.Shared.Network.Messages
{
    public class MsgConCmdAck : NetMessage
    {
        #region REQUIRED
        public const MsgGroups GROUP = MsgGroups.String;
        public static readonly string NAME = nameof(MsgConCmdAck);
        public MsgConCmdAck(INetChannel channel) : base(NAME, GROUP) { }
        #endregion

        public string Text { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer, bool isCompressed = false)
        {
            Text = buffer.ReadString();
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, bool useCompression = false)
        {
            buffer.Write(Text);
        }
    }
}
