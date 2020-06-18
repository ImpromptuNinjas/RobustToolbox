using System.IO;
using Lidgren.Network;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

#nullable disable

namespace Robust.Shared.Network.Messages
{
    public class MsgScriptResponse : NetMessage
    {
        #region REQUIRED

        public const MsgGroups GROUP = MsgGroups.Command;
        public const string NAME = nameof(MsgScriptResponse);

        public MsgScriptResponse(INetChannel channel) : base(NAME, GROUP)
        {
        }

        public int ScriptSession { get; set; }
        public bool WasComplete { get; set; }

        // Echo of the entered code with syntax highlighting applied.
        public FormattedMessage Echo { get; set; }
        public FormattedMessage Response { get; set; }

        #endregion

        public override unsafe void ReadFromBuffer(NetIncomingMessage buffer)
        {
            ScriptSession = buffer.ReadInt32();
            WasComplete = buffer.ReadBoolean();

            if (WasComplete)
            {
                var serializer = IoCManager.Resolve<IRobustSerializer>();

                var length = buffer.ReadVariableInt32();
                var bytes = buffer.ReadBytes(stackalloc byte[length]);
                fixed (byte* p = bytes)
                {
                    using var stream = new UnmanagedMemoryStream(p, bytes.Length, bytes.Length, FileAccess.Read);
                    Echo = serializer.Deserialize<FormattedMessage>(stream);
                    Response = serializer.Deserialize<FormattedMessage>(stream);
                }
            }
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            buffer.Write(ScriptSession);
            buffer.Write(WasComplete);

            if (WasComplete)
            {
                var serializer = IoCManager.Resolve<IRobustSerializer>();

                var memoryStream = new MemoryStream();
                serializer.Serialize(memoryStream, Echo);
                serializer.Serialize(memoryStream, Response);

                buffer.WriteVariableInt32((int)memoryStream.Length);
                buffer.Write(memoryStream.ToArray());
            }
        }
    }
}
