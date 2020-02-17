using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Lidgren.Network;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Serialization;
using Robust.Shared.IoC;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Network.Messages
{
    public class MsgViewVariablesModifyRemote : NetMessage
    {
        #region REQUIRED

        public const MsgGroups GROUP = MsgGroups.Command;
        public const string NAME = nameof(MsgViewVariablesModifyRemote);
        public MsgViewVariablesModifyRemote(INetChannel channel) : base(NAME, GROUP) { }

        #endregion

        /// <summary>
        ///     The session ID of the session to modify.
        /// </summary>
        public uint SessionId { get; set; }

        /// <summary>
        ///     Same deal as <see cref="ViewVariablesSessionRelativeSelector.PropertyIndex"/>.
        /// </summary>
        public object[] PropertyIndex { get; set; }

        /// <summary>
        ///     The new value of the property.
        /// </summary>
        public object Value { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer)
        {
            SessionId = buffer.ReadUInt32();
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            {
                var length = buffer.ReadVariableInt32();
                var bytes = buffer.ReadBytes(length);
                using (var stream = new MemoryStream(bytes))
                {
                    PropertyIndex = serializer.Deserialize<object[]>(stream);
                }
            }
            {
                var length = buffer.ReadVariableInt32();
                var bytes = buffer.ReadBytes(length);
                using (var stream = new MemoryStream(bytes))
                {
                    Value = serializer.Deserialize(stream);
                }
            }
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            var serializer = IoCManager.Resolve<IRobustSerializer>();
            buffer.Write(SessionId);
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, PropertyIndex);
                buffer.WriteVariableInt32((int)stream.Length);
                buffer.Write(stream.ToArray());
            }
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, Value);
                buffer.WriteVariableInt32((int)stream.Length);
                buffer.Write(stream.ToArray());
            }
        }
    }
}
