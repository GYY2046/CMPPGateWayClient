﻿using System;
using System.Net.Sockets;
using System.Threading;
using log4net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmsGatewayClient.CMPP.Messages;
using SmsGatewayClient.Common;
using SmsGatewayClient.Net;
using SmsGatewayClient.RateLimiters;
namespace SmsGatewayClient.CMPP
{
    /// <summary>
    /// 中国移动短信（China Mobile Peer to Peer）CMPP协议实现
    /// </summary>
    public class CmppConnection : SmsConnection
    {
        private readonly string cpId;
        private readonly string password;
        private readonly string serviceId;
        private readonly string appPhone;
        private readonly RollingWindowThrottler thrott = null;//new RollingWindowThrottler(20, TimeSpan.FromSeconds(1.0)); //先固定写死
        //private readonly ILog _log;
        /// <summary>
        /// 创建 CMPP 连接
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="cpId"></param>
        /// <param name="password"></param>
        /// <param name="serviceId"></param>
        /// <param name="appPhone"></param>
        public CmppConnection(string host, int port, string cpId, string password, string serviceId, string appPhone,RollingWindowThrottler thrott,ILog log)
            : base(host, port)
        {
            this.cpId = cpId;
            this.password = password;
            this.serviceId = serviceId;
            this.appPhone = appPhone;
            this.thrott = thrott;
            this._log = log;
        }

        /// <summary>
        /// 获取 SmsMessage 响应的 SequenceId
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        protected override uint ReadSequenceId(byte[] buffer)
        {
            return BitHelper.SubUInt32(buffer, CmppMessage.SequenceIdIndex);
        }

        /// <summary>
        /// 向运营商服务器验证CP用户名密码模板方法
        /// </summary>
        /// <returns></returns>
        public override uint LoginTemplate()
        {
            var timestamp = DateTime.Now.ToString("MMddHHmmss");
            var message = new CmppConnectMessage
                {
                    SequenceId = NextSequenceId(),
                    SourceAddr = cpId,
                    Timestamp = (uint)Convert.ToInt32(timestamp),
                    Version = 20,
                    AuthenticatorSource = CmppConnectMessage.Sign(cpId, password, timestamp),
                };

            var resp = new CmppConnectRespMessage(SendAndWait(socket, message));
            //Console.WriteLine("注册响应:"+resp.ToString());
            Assert.AreEqual(message.SequenceId, resp.SequenceId);

            return resp.Status;
        }

        /// <summary>
        /// 生成发送报文
        /// </summary>
        /// <param name="phones">手机号</param>
        /// <param name="content">内容</param>
        /// <returns></returns>
        protected override SmsMessage[] PackageMessages(string[] phones, string content)
        {
            var targetCount = (phones.Length - 1) / 100 + 1; // 群发短信最多支持100条

            var contentBytes = SmsMessage.Ucs2Encoding.GetBytes(content);
            var contentCount = (contentBytes.Length - 1) / 134 + 1; // 短信内容最多支持140字节

            var result = new CmppSubmitMessage[targetCount * contentCount];

            for (int i = 0; i < targetCount; i++)
            {
                var udhiId = (byte) Random.Next(byte.MaxValue);
                for (int j = 0; j < contentCount; j++)
                {
                    var message = new CmppSubmitMessage
                        {
                            SequenceId = NextSequenceId(),
                            PkTotal = (uint)contentCount,
                            PkNumber = (uint)(j + 1),
                            ServiceId = serviceId,
                            MsgFmt = 8,
                            FeeType = "01",
                            MsgSrc = cpId,
                            SrcId = appPhone,
                            DestUserTl = (uint)Math.Min(100, phones.Length - i * 100)
                        };
                    message.DestTerminalId = new string[message.DestUserTl];
                    Array.Copy(phones, i * 100, message.DestTerminalId, 0, (int)message.DestUserTl);
                    Udhi(message, contentBytes, j, contentCount,udhiId);

                    result[i * j + j] = message;
                }
            }

            return result;
        }

        /// <summary>
        /// 向运营商服务器发送短信模板方法
        /// </summary>
        /// <returns></returns>
        protected override uint SubmitTemplate(SmsMessage message)
        {
            long waitTime = 0;
            while (thrott.ShouldThrottle(out waitTime))
            {
                _log.InfoFormat("底层需要暂停:{0} ", waitTime);
                System.Threading.Thread.Sleep((int)waitTime);
            }
            _log.InfoFormat("底层发送:{0}",message.GetSequenceId());
            var receiveByte = SendAndWait(socket, message);
            var resp = new CmppSubmitRespMessage(receiveByte);
            _log.InfoFormat("底层发送完毕:{0},resp:{1}", message.GetSequenceId(),resp.SequenceId);
            Assert.AreEqual(((CmppSubmitMessage)message).SequenceId, resp.SequenceId);
            return resp.Result;

        }

        /// <summary>
        /// 向运营商发送链路检测包保持连接
        /// </summary>
        /// <param name="smsSocket"></param>
        protected override void Heartbeat(SmsSocket smsSocket)
        {
            var message = new CmppActiveTestMessage
            {
                SequenceId = NextSequenceId()
            };

            var resp = new CmppActiveTestRespMessage(SendAndWait(smsSocket, message));
            Assert.AreEqual(message.SequenceId, resp.SequenceId);
            //每过一段时间发送一段心跳检测
            Thread.Sleep(60 * 1000); // TODO: 配置
        }

        /// <summary>
        /// 解析请求报文
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        protected override SmsMessage Handle(byte[] buffer)
        {
            uint commandId = BitHelper.SubUInt32(buffer, CmppMessage.CommandIdIndex);
            switch (commandId)
            {
                case CmppCommandId.CMPP_ACTIVE_TEST:
                    {
                        var message = new CmppActiveTestMessage(buffer);
                        var ack = new CmppActiveTestRespMessage
                        {
                            SequenceId = message.SequenceId
                        };
                        return ack;
                    }
                default:
                    return null; // throw new NotImplementedException("UnHandleRequest: " + commandId);
            }
        }
    }
}
