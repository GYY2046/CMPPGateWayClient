using System;
using System.Collections.Generic;
using System.Net.Sockets;
using log4net;

namespace SmsGatewayClient.Net
{
    /// <summary>
    /// Socket 管理
    /// </summary>
    public class SocketManager
    {
        private static readonly byte[] locker = new byte[0];

        private static readonly Dictionary<string, SmsSocket> sockets = new Dictionary<string, SmsSocket>();
        public static ILog _log;

        /// <summary>
        /// 获取指定主机的 Socket 连接
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="trafficControl"></param>
        /// <returns></returns>
        public static SmsSocket Get(string host, int port, int trafficControl)
        {
            var key = host + port;
            if (sockets.ContainsKey(key))
            {
                return sockets[key];
            }

            lock (locker)
            {
                if (sockets.ContainsKey(key))
                {
                    return sockets[key];
                }

                var socket = new SmsSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, trafficControl);
                socket.Connect(host, port);
                sockets.Add(key, socket);
                
                if (_log != null)
                {
                    _log.InfoFormat("{0}-{1}：初始化连接",DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff"),key);
                }
                return socket;
            }
        }
        /// <summary>
        /// 判断socket是否连接
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static bool IsSocketConnected(SmsSocket s)
        {
            try
            {
                bool part1 = s.Poll(1000, SelectMode.SelectRead);
                bool part2 = (s.Available == 0);
                if (part1 && part2)
                    return false;
                else
                    return true;
            }
            catch (Exception ex)
            {
                _log.ErrorFormat("socket连接异常：{0}", ex);
                return false;
            }
            //if (s == null)
            //    return false;
            //return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
        }
        /// <summary>
        /// 连接断开后只能重连
        /// </summary>
        /// <param name="socket"></param>
        /// <returns></returns>
        public static SmsSocket Reconnect(SmsSocket socket)
        {
            lock (locker)
            {                
                if(IsSocketConnected(socket))
                   return socket;
                else
                {
                    int traffic = socket.TrafficControl;
                    var endpoint = socket.RemoteEndPoint;
                    var newSocket = new SmsSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, traffic)
                    {
                        KeepAlive = socket.KeepAlive
                    };
                    //连接之前断开之前的连接释放资源
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Disconnect(true);
                    socket.Close(); 

                    newSocket.Connect(endpoint);
                    if (_log != null)
                    {
                        _log.InfoFormat("{0}：{1}重新建立连接", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff"), endpoint.ToString());
                    }
                    sockets[endpoint.ToString()] = newSocket;
                    return newSocket; 
                }
            }
            //if (!IsSocketConnected(socket))
            //{
            //    int traffic = socket.TrafficControl;
            //    var endpoint = socket.RemoteEndPoint;              
            //    var newSocket = new SmsSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, traffic)
            //    {
            //        KeepAlive = socket.KeepAlive
            //    };
            //    //socket.Shutdown(SocketShutdown.Both);
            //    //socket.Disconnect(true);
            //    //socket.Close();  

            //    //newSocket.Connect(socket.RemoteEndPoint);
            //    newSocket.Connect(endpoint);
            //    if (_log != null)
            //    {
            //        _log.InfoFormat("{0}：{1}重新建立连接", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:ffff"), endpoint.ToString());
            //    }
            //    sockets[endpoint.ToString()] = newSocket;
            //    return  newSocket; 
            //}
            //return socket;
        }

        /// <summary>
        /// 清除上次请求的数据
        /// </summary>
        /// <param name="args"></param>
        public static void Clear(SocketAsyncEventArgs args)
        {
            Array.Clear(args.Buffer, 0, args.Buffer.Length); // 清空之前的数据
            args.UserToken = null;
            args.AcceptSocket = null;            
        }
    }
}