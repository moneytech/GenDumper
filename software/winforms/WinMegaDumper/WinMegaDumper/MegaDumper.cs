﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinCartDumper
{
    public enum MegaDumperOperation : byte
    {
        None = (byte)'\0',
        Version = (byte)'v',
        Dump = (byte)'d',
        Header = (byte)'h',
        Autodetect = (byte)'a'
    }

    public enum ReturnCode : int
    {
        OK = 0,
        ERROR = -1,
        NOT_FOUND = 1
    }

    public struct MegaDumperResult
    {
        public MegaDumperOperation operation;
        public object result;
        public DateTime dtStart;
        public DateTime dtEnd;
        public ReturnCode returnCode;
    }

    class MegaDumper : BackgroundWorker
    {
        private SerialPort serialPort;
        private List<byte> bytesStream;
        private uint bytesToReceive;
        private string port;
        private MegaDumperOperation operation;

        private System.Object transmissionOverLock;
        private System.Object transmissionAliveLock;

        private static RomHeader romHeader;

        private const string FIRMWARE_STRING = "GENDUMPER";


        public MegaDumperOperation Operation
        {
            get { return operation; }
            set { operation = value; }
        }

        /// <summary>
        /// Because the Sega Mega Drive endianness does not match x86, we need to reverse bytes of any number conversion to get the actual value.
        /// Works for 16 bit and 32 bit unsigned integers.
        /// </summary>
        /// <param name="x"></param>
        /// <returns>The byteswipped value of the integer</returns>
        public static ushort SwapBytes(ushort x)
        {
            return (ushort)((ushort)((x & 0xff) << 8) | ((x >> 8) & 0xff));
        }

        /// <summary>
        /// Because the Sega Mega Drive endianness does not match x86, we need to reverse bytes of any number conversion to get the actual value.
        /// Works for 16 bit and 32 bit unsigned integers.
        /// </summary>
        /// <param name="x"></param>
        /// <returns>The byteswipped value of the integer</returns>
        public static uint SwapBytes(uint x)
        {
            return ((x & 0x000000ff) << 24) +
                   ((x & 0x0000ff00) << 8) +
                   ((x & 0x00ff0000) >> 8) +
                   ((x & 0xff000000) >> 24);
        }


        public MegaDumper()
        {
            serialPort = new SerialPort();
            bytesStream = new List<byte>();
            port = "";
            bytesToReceive = 0;
            operation = MegaDumperOperation.None;

            this.WorkerReportsProgress = true;

            transmissionOverLock = new Object();
            transmissionAliveLock = new Object();

            romHeader = new RomHeader();

            serialPort.BaudRate = 460800;
            serialPort.Parity = Parity.None;
            serialPort.DataBits = 8;
            serialPort.StopBits = StopBits.One;
            serialPort.ReadTimeout = 500;
            serialPort.WriteTimeout = 500;
            serialPort.DataReceived += new SerialDataReceivedEventHandler(serialPort_DataReceived);
        }

        public string Port
        {
            get { return port; }
            set { port = value; }
        }


        public MegaDumperResult AutoDetect()
        {
            MegaDumperResult mdr = new MegaDumperResult();
            mdr.dtStart = DateTime.Now;
            mdr.returnCode = ReturnCode.NOT_FOUND;
            mdr.operation = MegaDumperOperation.Autodetect;


            string[] ports = SerialPort.GetPortNames();
            foreach (string p in ports)
            {
                port = p;
                string v = GetVersion();
                if (v.StartsWith(MegaDumper.FIRMWARE_STRING))
                {
                    mdr.returnCode = ReturnCode.OK;
                    mdr.result = p; //return named serial port in the result
                    break;
                }
            }

            mdr.dtEnd = DateTime.Now;
            return mdr;
        }

        public string GetVersion()
        {
            string ret = "";

            try
            {
                try
                {
                    serialPort.PortName = port;
                    bytesToReceive = 0;
                    bytesStream.Clear();
                    //operation = MegaDumperOperation.Version;

                    /* build version command */
                    byte[] command = new byte[1];
                    command[0] = (byte)'v';

                    serialPort.Open();
                    lock (transmissionOverLock)
                    {
                        serialPort.Write(command, 0, command.Length);
                        if (!Monitor.Wait(transmissionOverLock, 500))
                        {
                            throw new TimeoutException();
                        }
                        else
                        {
                            //business as normal
                        }
                    }

                }
                catch (System.IO.IOException ioe)
                {
                    //most likely COM1 or another port than failed at the "Open" step
                }
                catch (TimeoutException te)
                {
                    //most likely a connected thing which is not a mega dumper
                }
                catch (Exception e)
                {
                    // 🤷 ¯\_(ツ)_/¯ 🤷
                }
                finally
                {
                    serialPort.Close();
                }
            }
            catch { }
           

            ret = Encoding.ASCII.GetString(bytesStream.ToArray());

            return ret;

        }

        public MegaDumperResult GetDump()
        {
            return GetDump(0, 0);
        }
        public MegaDumperResult GetDump(uint from, uint to)
        {
            //set port to whatever was selected last by user
            serialPort.PortName = port;

            // Cart object
            Cart cart = new Cart();

            // result object
            MegaDumperResult res = new MegaDumperResult();
            res.operation = operation;
            res.dtStart = DateTime.Now;

            // dump command
            byte[] command = new byte[9];
            byte[] fromBytes, toBytes;
            command[0] = (byte)'d';

            // reset data buffer
            bytesToReceive = 0;
            bytesStream.Clear();

            //if from and to == 0, we try to try to get the rom header first
            if(from == 0 && to == 0)
            {
                fromBytes = BitConverter.GetBytes(RomHeader.PHYSICAL_ADDR_START); /* rom header starts at physical address 0x80 */
                toBytes = BitConverter.GetBytes(RomHeader.PHYSICAL_ADDR_END); /* rom header ends at physical address 0x100 */
                fromBytes.CopyTo(command, 1); /* from address */
                toBytes.CopyTo(command, 5); /* to address */

                serialPort.Open();
                lock (transmissionOverLock)
                {
                    serialPort.Write(command, 0, command.Length);
                    if (!Monitor.Wait(transmissionOverLock))
                    {
                        //timeout
                    }
                    else
                    {
                        //business as normal
                    }
                }
                serialPort.Close();

                cart.SetHeaderFromRawData(bytesStream.ToArray());

                // report this progress
                this.ReportProgress(0, cart);


                // reset data buffer in order to receive the actual rom content
                bytesToReceive = 0;
                bytesStream.Clear();

                //finally get the proper rom start and end
                fromBytes = BitConverter.GetBytes(cart.Header.RomAddressStart);
                toBytes = BitConverter.GetBytes(cart.Header.RomAddressEnd >> 1);
            }
            else
            {
                fromBytes = BitConverter.GetBytes(from);
                toBytes = BitConverter.GetBytes(to);
            }


            
            
            fromBytes.CopyTo(command, 1); /* from address */
            toBytes.CopyTo(command, 5); /* to address */

            serialPort.Open();
            lock (transmissionOverLock)
            {
                serialPort.Write(command, 0, command.Length);
                if (!Monitor.Wait(transmissionOverLock))
                {
                    //timeout
                }
                else
                {
                    //business as normal
                }
            }
            serialPort.Close();

            //save dump to cart
            cart.RomData = bytesStream.ToArray();

            //set the cart as result
            res.result = cart;
            res.dtEnd = DateTime.Now;
            return res;

        }

        public RomHeader getRomHeader()
        {
            serialPort.PortName = port;
            bytesToReceive = 0;
            bytesStream.Clear();
            //operation = MegaDumperOperation.Header;

            serialPort.Open();
            

            lock (transmissionOverLock)//waits N seconds for a condition variable
            {
                serialPort.Write("i");

                if (!Monitor.Wait(transmissionOverLock, 3000))
                {
                    //timeout
                }
                else
                {
                    //business as normal
                }
            }



            serialPort.Close();

            RomHeader header = new RomHeader();
            header.parse(bytesStream.ToArray());
            return header;


        }

       

        private void serialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int intBuffer = serialPort.BytesToRead;
            byte[] byteBuffer = new byte[intBuffer];
            serialPort.Read(byteBuffer, 0, intBuffer);
            bytesStream.AddRange(byteBuffer);

            lock (transmissionAliveLock)
            {
                Monitor.Pulse(transmissionAliveLock);
            }



            if (bytesToReceive == 0)
            {
                //beginning of a new stream, the first 4 bytes contains a uint32 that contains the size of the stream
                if (bytesStream.Count >= 4)
                {
                    //convert the first 4 bytes to uint32
                    byte[] rawBytesToReceive = new byte[4];
                    rawBytesToReceive[0] = bytesStream[0];
                    rawBytesToReceive[1] = bytesStream[1];
                    rawBytesToReceive[2] = bytesStream[2];
                    rawBytesToReceive[3] = bytesStream[3];
                    bytesToReceive = BitConverter.ToUInt32(rawBytesToReceive, 0);

                    //remove the first four bytes from the stream
                    bytesStream.RemoveRange(0, 4);
                }

                this.ReportProgress(0);
            }
            else
            {

                if(operation != MegaDumperOperation.Autodetect)
                {
                    int progress = (int)((float)bytesStream.Count / (float)bytesToReceive * 100.0f);
                    this.ReportProgress((int)progress);
                }

                //this.ReportProgress(50);
            }
            if (bytesStream.Count >= bytesToReceive)
            {

                lock (transmissionOverLock)
                {
                    Monitor.Pulse(transmissionOverLock);
                }
            }

            
        }
    }
}
