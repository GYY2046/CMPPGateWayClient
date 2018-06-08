using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SmsGatewayClient.CMPP.Messages
{
    internal class CmppRespHeader : CmppMessage
    {
        public CmppRespHeader(byte[] buffer)
            : base(buffer)
        { 
            
        }
        /// <summary>
        /// 判断是否是正确的消息头
        /// </summary>
        /// <returns></returns>
        public bool IsRespHeader()
        {
            switch (CommandId)
            {
                case CmppCommandId.CMPP_CONNECT_RESP:
                case CmppCommandId.CMPP_TERMINATE_RESP:
                case CmppCommandId.CMPP_SUBMIT_RESP:
                case CmppCommandId.CMPP_DELIVER_RESP:
                case CmppCommandId.CMPP_QUERY_RESP:
                case CmppCommandId.CMPP_CANCEL_RESP:
                case CmppCommandId.CMPP_ACTIVE_TEST_RESP:
                case CmppCommandId.CMPP_FWD_RESP:
                case CmppCommandId.CMPP_MT_ROUTE_RESP:
                case CmppCommandId.CMPP_MO_ROUTE_RESP:
                case CmppCommandId.CMPP_GET_ROUTE_RESP:
                case CmppCommandId.CMPP_MT_ROUTE_UPDATE_RESP:
                case CmppCommandId.CMPP_MO_ROUTE_UPDATE_RESP:
                case CmppCommandId.CMPP_PUSH_MT_ROUTE_UPDATE_RESP:
                case CmppCommandId.CMPP_PUSH_MO_ROUTE_UPDATE_RESP:
                    return true;
                default:
                    return false;

            }
        }
    }
}
