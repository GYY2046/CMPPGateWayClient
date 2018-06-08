using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using log4net;
using SmsGatewayClient.CMPP.Messages;
using SmsGatewayClient.Common;
using SmsGatewayClient.Net;

namespace SmsGatewayClient
{
    /// <summary>
    /// 发送短信报文
    /// </summary>
    public abstract class SmsConnection : IDisposable
    {
        private const int Size = 30;

        private static readonly byte[] sidLocker = new byte[0];
        private static long sequenceId;

        protected static readonly Random Random = new Random();

        private static readonly Queue<SocketAsyncEventArgs> sendPool = new Queue<SocketAsyncEventArgs>();
        private static readonly Queue<SocketAsyncEventArgs> receivePool = new Queue<SocketAsyncEventArgs>();

        private static readonly byte[] msgLocker = new byte[0];
        private static readonly Dictionary<uint, WaitingDataToken> messageBuffer = new Dictionary<uint, WaitingDataToken>();

        internal static int trySending;
        internal static int totalSend;
        internal static int tryReceiving;
        internal static int totalReceived;

        protected SmsSocket socket;
        private bool disposed;
        protected ILog _log;
        public delegate void OnServerDataReceived(byte[] receiveBuff);
        public event OnServerDataReceived ServerDataHandler;
        //protected List<string> messageList = new List<string>();
        //BufferManager m_bufferManager;
        //定义接收数据的对象  
        static List<byte> m_buffer = new List<byte>();
        static readonly object obj = new object();
        //static ConcurrentQueue<byte>  
        /// <summary>
        /// 当前运行状态
        /// </summary>
        public string Status
        {
            get
            {
                return string.Format("Total Send:\t{0} - {1}\nTotal Receive:\t{2} - {3}", trySending, totalSend, tryReceiving, totalReceived);
            }
        }

        /// <summary>
        /// 连接 socket
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        protected SmsConnection(string host, int port, ILog log=null)
        {
            _log = log;
            SocketManager._log = log;
            socket = SocketManager.Get(host, port, TrafficControl);
            ServerDataHandler += HandleCap;
            //m_buffer = new List<byte>();  
            //messageList = SocketManager.ConnectMessageList;
        }

        /// <summary>
        /// 流水号
        /// </summary>
        /// <returns></returns>
        public static uint NextSequenceId()
        {
            lock (sidLocker)
            {
                sequenceId++;
                if (sequenceId > UInt32.MaxValue)
                {
                    sequenceId = 1;
                }
                return (uint)sequenceId;
            }
        }

        /// <summary>
        /// 向运营商服务器验证CP用户名密码
        /// </summary>
        /// <returns></returns>
        public int Login()
        {
            if (socket.IsLogin)
            {
                return 0;
            }

            lock (socket.Locker)
            {
                if (socket.IsLogin)
                {
                    return 0;
                }

                if (!socket.Connected)
                {
                    socket = SocketManager.Reconnect(socket);
                }

                int status = (int)LoginTemplate();

                socket.IsLogin = (status == 0);

                if (socket.IsLogin)
                {
                    if (socket.KeepAlive == null || !socket.KeepAlive.IsAlive)
                    {
                        socket.KeepAlive = KeepAlive();
                        socket.KeepAlive.Start(socket);
                    }
                }
                return status;
            }
        }

        private Thread KeepAlive()
        {
            return new Thread(state =>
                {
                    var selfSocket = (SmsSocket)state;
                    //var selfSocket = socket;
                    while (selfSocket != null)
                    {
                        //if (!selfSocket.Connected)                           
                        if(!SocketManager.IsSocketConnected(selfSocket))
                        {
                            selfSocket.IsLogin = false;
                            break;
                        }
                        try
                        {
                            Heartbeat(selfSocket);
                        }
                        catch (Exception ex)
                        {
                            _log.InfoFormat("{0}-{1}心跳数据发送异常", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff"), ex);
                            if (selfSocket != null)
                            {                              
                                selfSocket.IsLogin = false;
                            }
                            else
                                break;
                        }
                        //selfSocket = socket;
                    }
                });
        }

        /// <summary>
        /// 向运营商服务器验证CP用户名密码模板方法
        /// </summary>
        /// <returns></returns>
        public abstract uint LoginTemplate();

        /// <summary>
        /// 向运营商服务器发送短信
        /// </summary>
        /// <param name="phones">手机号列表</param>
        /// <param name="content">内容</param>
        /// <returns></returns>
        public int Submit(string[] phones, string content)
        {
            int result = 0;
            if (!socket.Connected)
            {                
                socket = SocketManager.Reconnect(socket);
            }

            if (!socket.IsLogin)
            {
                int status = Login();
                if (status != 0)
                {
                    return status;
                }
            }

            var messages = PackageMessages(phones, content);

            if (messages.Length == 1)
            {
                return (int)SubmitTemplate(messages[0]);
            }

            foreach (var message in messages)
            {
                var tmp = SubmitTemplate(message);
                if (tmp != 0)
                    result = (int)tmp;
            }

            return result;
        }

        /// <summary>
        /// 生成发送报文
        /// </summary>
        /// <param name="phones">手机号</param>
        /// <param name="content">内容</param>
        /// <returns></returns>
        protected abstract SmsMessage[] PackageMessages(string[] phones, string content);

        /// <summary>
        /// 向运营商服务器发送短信模板方法
        /// </summary>
        /// <returns></returns>
        protected abstract uint SubmitTemplate(SmsMessage message);

        /// <summary>
        /// 向运营商发送链路检测包保持连接
        /// </summary>
        /// <param name="smsSocket"></param>
        protected abstract void Heartbeat(SmsSocket smsSocket);

        /// <summary>
        /// 运营商对同一连接上滑动时间窗（并发请求数量）有限制
        /// </summary>
        protected virtual int TrafficControl
        {
            get { return 16; }
        }

        /// <summary>
        /// 获取 SmsMessage 响应的 SequenceId
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        protected abstract uint ReadSequenceId(byte[] buffer);

        /// <summary>
        /// 解析请求报文
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        protected virtual SmsMessage Handle(byte[] buffer)
        {
            return null;
        }

        /// <summary>
        /// 异步发送不等待完成
        /// </summary>
        /// <param name="ack"></param>
        private void Send(SmsMessage ack)
        {
            if (disposed)
            {
                return;
            }
            SocketAsyncEventArgs sendArgs;
            try
            {
                lock (((ICollection)sendPool).SyncRoot)
                {
                    sendArgs = sendPool.Dequeue();
                }
            }
            catch (InvalidOperationException) // 队列为空
            {
                sendArgs = new SocketAsyncEventArgs();
                sendArgs.Completed += AfterSend;
            }

            var buffer = ack.ToBytes();
            sendArgs.SetBuffer(buffer, 0, buffer.Length); // 填充要发送的数据

            Interlocked.Increment(ref trySending);

            if (!socket.SendAsync(sendArgs)) // 未异步发送，应当在同步上下文中处理
            {
                AfterSend(null, sendArgs);
            }
        }
        /// <summary>
        /// 异步发送并且等待接收数据完成  此方法存在如果一条消息超时了导致后面所有的消息都会超时
        /// </summary>
        /// <param name="smsSocket"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        protected byte[] SendAndWait(SmsSocket smsSocket, SmsMessage message)
        {
            if (smsSocket == null)
            {
                return null;
            }
            //if (!socket.Connected)
            if (!SocketManager.IsSocketConnected(smsSocket))
            {
                _log.InfoFormat("Socket未连接-SendAndWait");
                //return null;
                //socket = SocketManager.Reconnect(socket);
                //messageList.Add(string.Format("{0}:Socket未连接-SendAndWait", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff")));
            }
            if (!smsSocket.IsLogin)
            {                
                _log.InfoFormat("Socket未登录-SendAndWait");
                //messageList.Add(string.Format("{0}:Socket未登录-SendAndWait", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff")));
            }
            SocketAsyncEventArgs sendArgs;
            try
            {
                smsSocket.WaitTraffic();
                lock (((ICollection)sendPool).SyncRoot)
                {
                    sendArgs = sendPool.Dequeue();
                }
            }
            catch (InvalidOperationException) // 队列为空
            {
                sendArgs = new SocketAsyncEventArgs();
                sendArgs.Completed += AfterSend;
            }

            // 发送完加入等待回复的队列
            var token = new WaitingDataToken { SequenceId = message.GetSequenceId() };
            lock (msgLocker)
            {
                messageBuffer.Add(token.SequenceId, token);
            }

            sendArgs.AcceptSocket = smsSocket;
            sendArgs.UserToken = token;
            var buffer = message.ToBytes();
            sendArgs.SetBuffer(buffer, 0, buffer.Length); // 填充要发送的数据

            Interlocked.Increment(ref trySending);

            if (!smsSocket.SendAsync(sendArgs)) // 未异步发送，应当在同步上下文中处理
            {
                AfterSend(null, sendArgs);
            }
            //_log.InfoFormat("等待：{0}", token.SequenceId);
            WaitHandle.WaitAll(new WaitHandle[] { token.WaitHandle }, 60 * 1000); // 等待异步请求结束  65 * 1000
            //_log.InfoFormat("收到：{0}", token.SequenceId);
            lock (msgLocker)
            {
                messageBuffer.Remove(token.SequenceId);
            }

            if (token.Bytes == null)
            {
                token.SocketError = SocketError.TimedOut;
            }

            smsSocket.IsLogin = smsSocket.Connected;
            if (token.SocketError != SocketError.Success)
            {
                throw new SocketException((int)token.SocketError);
            }
            return token.Bytes;
        }

        /// <summary>
        /// 发送完 Message 后释放资源
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AfterSend(object sender, SocketAsyncEventArgs e)
        {
            var smsSocket = (SmsSocket)e.AcceptSocket;
            smsSocket.ReleaseTraffic();

            Interlocked.Increment(ref totalSend);

            var token = (WaitingDataToken)e.UserToken;

            if (token != null)
            {
                if (e.SocketError != SocketError.Success)
                {
                    token.SocketError = e.SocketError;
                    token.WaitHandle.Set();
                }
                else
                {
                    Receive(smsSocket);
                }
            }

            if (sendPool.Count < Size)
            {
                SocketManager.Clear(e);
                lock (((ICollection)sendPool).SyncRoot)
                {
                    sendPool.Enqueue(e);
                }
            }
            else
            {
                e.Dispose();
            }
        }

        /// <summary>
        /// 开始接收数据
        /// </summary>
        /// <param name="smsSocket"></param>
        private void Receive(Socket smsSocket)
        {
            SocketAsyncEventArgs receiveArgs;
            try
            {
                lock (((ICollection)receivePool).SyncRoot)
                {
                    receiveArgs = receivePool.Dequeue(); // 从连接池中取出 receiveArgs
                }
            }
            catch (InvalidOperationException)
            {
                receiveArgs = new SocketAsyncEventArgs();
                receiveArgs.UserToken = smsSocket;
                receiveArgs.SetBuffer(new byte[84], 0, 84); // 缓冲区大小原先设置为64 会出现同时到两个请求导致一个包被拆分到两个请求里面
                receiveArgs.Completed += AfterReceive;
            }

            Interlocked.Increment(ref tryReceiving);

            if (!smsSocket.ReceiveAsync(receiveArgs))
            {
                AfterReceive(null, receiveArgs);
            }
        }

        private void DoReceiveEvent(byte[] buff)
        {
            if (ServerDataHandler == null)
                return;
            Thread thread = new Thread(new ParameterizedThreadStart((obj) =>
            {
                ServerDataHandler((byte[])obj);
            }));
            thread.IsBackground = true;
            thread.Start(buff);
        }
        private void HandleCap(byte[] capBytes)
        {
            uint seqId = ReadSequenceId(capBytes);

            if (!messageBuffer.ContainsKey(seqId))
            {
                var ack = Handle(capBytes);
                if (ack != null)
                {
                    Send(ack);
                }
                return;
            }
            Interlocked.Increment(ref totalReceived);
            var token = messageBuffer[seqId];
            token.Bytes = capBytes;
            token.WaitHandle.Set();
            //_log.InfoFormat("释放：{0},Status:{1}", seqId, Status);
        }
        //多线程处理有问题重新实现
        //private void AfterReceive(object sender, SocketAsyncEventArgs e)
        //{
        //    if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
        //    {
        //            //读取数据  
        //            byte[] data = new byte[e.BytesTransferred];                    
        //            //_log.InfoFormat("接收到的包长度:{0}-时间：{1}", e.BytesTransferred,DateTime.Now.Ticks);                   
        //            lock (m_buffer)
        //            {
        //                Array.Copy(e.Buffer, e.Offset, data, 0, e.BytesTransferred);
        //                m_buffer.AddRange(data);
        //            }
        //            //if (e.BytesTransferred < 21)
        //            //{
        //            //    var buff_1 = BitConverter.ToString(e.Buffer);
        //            //    var buff_2 = BitConverter.ToString(data);
        //            //    var buff_3 = BitConverter.ToString(m_buffer.ToArray());
        //            //    _log.InfoFormat("Buffer:{0}\r\n-Data:{1}\r\n-M_buff:{2}", buff_1, buff_2, buff_3);
        //            //}
        //            do
        //            {
        //                if (m_buffer.Count <= 12)
        //                {
        //                    //_log.InfoFormat("不够一个消息头需要继续接收:{0}", m_buffer.Count);
        //                    break;
        //                }
        //                byte[] lenBytes = new byte[4];
        //                lock (m_buffer)
        //                {
        //                    if (m_buffer.Count >= 4)
        //                    {
        //                        lenBytes = m_buffer.GetRange(0, 4).ToArray();
        //                    }
        //                    else
        //                        break;
        //                }
        //                int packageLen = (int)BitHelper.SubUInt32(lenBytes, 0);
        //                if (packageLen <= m_buffer.Count)
        //                {
        //                    //包够长时,则提取出来,交给后面的程序去处理  
        //                    byte[] rev = new byte[packageLen];
        //                    lock (m_buffer)
        //                    {
        //                        if (m_buffer.Count >= packageLen)
        //                        {
        //                            rev = m_buffer.GetRange(0, packageLen).ToArray();
        //                            m_buffer.RemoveRange(0, packageLen);
        //                        }
        //                        else 
        //                        {
        //                            break;
        //                        }
        //                    }
        //                    //将数据包交给前台去处理  
        //                    HandleCap(rev);
        //                }
        //                else
        //                {   //长度不够,还得继续接收,需要跳出循环  
        //                    //_log.InfoFormat("包长度不够:{0},{1}", packageLen, m_buffer.Count);
        //                    //lock (m_buffer)
        //                    //{
        //                    //    var buff_2 = BitConverter.ToString(data);
        //                    //    var buff_3 = BitConverter.ToString(m_buffer.ToArray());
        //                    //    _log.InfoFormat("接收内容：{0}\r\n当前内容：{1}",buff_2, buff_3);
        //                    //}
        //                    break;
        //                }
        //            } while (m_buffer.Count > 4);
        //            //if (m_buffer.Count > 0)
        //            //{
        //            //    _log.InfoFormat("本次未处理长度:{0}", m_buffer.Count);
        //            //}

        //        if (receivePool.Count < Size)
        //        {
        //            SocketManager.Clear(e);
        //            lock (((ICollection)receivePool).SyncRoot)
        //            {
        //                receivePool.Enqueue(e);
        //            }
        //        }
        //        else
        //        {
        //            e.Dispose();
        //        }
        //    }
        //}

        private void AfterReceive(object sender, SocketAsyncEventArgs e)
        {
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                //读取数据  
                byte[] data = new byte[e.BytesTransferred];
                //lock (m_buffer)
                //{
                try
                {
                    Array.Copy(e.Buffer, e.Offset, data, 0, e.BytesTransferred);
                    lock (obj)
                    {
                        m_buffer.AddRange(data);
                        do
                        {
                            if (m_buffer.Count < 12)
                                break;
                            CmppRespHeader header = new CmppRespHeader(m_buffer.GetRange(0, 12).ToArray());
                            if (!header.IsRespHeader())
                            {
                                _log.ErrorFormat("接收到的消息体不合法，直接丢弃。丢弃的包大小:{0}", m_buffer.Count);
                                m_buffer.Clear();
                                break;
                            }
                            byte[] lenBytes = new byte[4];
                            lenBytes = m_buffer.GetRange(0, 4).ToArray();
                            int packageLen = (int)BitHelper.SubUInt32(lenBytes, 0);
                            //_log.ErrorFormat("解析的数据包大小:{0}", packageLen);
                            if (packageLen <= m_buffer.Count)
                            {
                                byte[] rev = new byte[packageLen];
                                rev = m_buffer.GetRange(0, packageLen).ToArray();
                                m_buffer.RemoveRange(0, packageLen);
                                //if (m_buffer.Count >= packageLen)
                                //{
                                //    //lock (m_buffer)
                                //    //{
                                //        rev = m_buffer.GetRange(0, packageLen).ToArray();
                                //        m_buffer.RemoveRange(0, packageLen);
                                //    //}
                                //}
                                //else
                                //{
                                //    break;
                                //}
                                //HandleCap(rev);
                                DoReceiveEvent(rev);
                            }
                            else
                            {   //长度不够,还得继续接收,需要跳出循环  
                                break;
                            }
                        } while (m_buffer.Count >= 12);
                    }
                }
                catch (Exception ex)
                {
                    _log.ErrorFormat("解析报数据出现异常：{0}", ex);
                    m_buffer.Clear();
                }
                finally
                {
                    //}
                    if (receivePool.Count < Size)
                    {
                        SocketManager.Clear(e);
                        lock (((ICollection)receivePool).SyncRoot)
                        {
                            receivePool.Enqueue(e);
                        }
                    }
                    else
                    {
                        e.Dispose();
                    }
                }
            }
        }
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            disposed = true;
            socket = null;
        }

        /// <summary>
        /// UDHI 长短信拆分
        /// </summary>
        /// <param name="message">短信</param>
        /// <param name="content">内容</param>
        /// <param name="no">第N条</param>
        /// <param name="count">共M条</param>
        /// <param name="udhiId">本条消息唯一标识</param>
        /// <param name="limit">字数限制（默认140）</param>
        /// <param name="headLength">udhi头长度（默认为6）</param>
        protected virtual void Udhi(ISubmitMessage message, byte[] content, int no, int count, byte[] udhiId, int limit = 140, int headLength = 6)
        {
            if (count == 1)
            {
                message.MsgContent = content;
            }
            else // 长短信
            {
                var index = no * (limit - headLength);

                var length = Math.Min(limit - headLength, content.Length - index);
                message.TpUdhi = 1;
                message.MsgContent = new byte[length + headLength];
                //  6位协议头格式：05 00 03 XX MM NN
                //message.MsgContent[0] = 0x05;
                //message.MsgContent[2] = 0x03;
                //message.MsgContent[3] = udhiId;
                //message.MsgContent[4] = (byte)count;
                //message.MsgContent[5] = (byte)(no + 1);
                //使用 7 位的协议头格式：06 08 04 XX XX MM NN
                message.MsgContent[0] = 0x06;
                message.MsgContent[1] = 0x08;
                message.MsgContent[2] = 0x04;
                message.MsgContent[3] = udhiId[0];          //C#  字节存储是小端顺序
                message.MsgContent[4] = udhiId[1];
                message.MsgContent[5] = (byte)count;
                message.MsgContent[6] = (byte)(no + 1);
                //if (no == 0)
                //{
                //    Array.Copy(content, 0, message.MsgContent, 6, length);
                //}
                //else
                //{
                //    Array.Copy(content, index, message.MsgContent, 6, length);
                //}
                Array.Copy(content, index, message.MsgContent, headLength, length);
                //var tmp = new byte[length];
                //Array.Copy(content, index, tmp, 0, length);
                //var str = SmsMessage.Ucs2Encoding.GetString(tmp);
                //_log.InfoFormat("发送信息：{0}", str);                
            }
            message.MsgLength = (uint)message.MsgContent.Length;
        }
    }
}