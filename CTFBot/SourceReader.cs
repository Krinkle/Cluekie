using System;
using System.Collections;
using System.Collections.Specialized;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using log4net;

namespace CTFBot
{
	struct SourceEvent
	{
		public enum EventType
		{
			cluereport, somethingelse
		}

		public string fullstring;
		public string sendname;
		
		public EventType eventtype;
		
		public string title;
		public string user;
		public string url;
		public string score;
		public string reverted;
		public string reason;
		public string time;

		public override string ToString()
		{
			return "" + fullstring + "";
		}
	}

	class SourceReader
	{
		public IrcClient sourceirc = new IrcClient();
		public DateTime lastMessage = DateTime.Now;
		public string lastError = "";

		// RC parsing regexen
		static Regex stripColours = new Regex(@"\x04\d{0,2}\*?");
		static Regex stripBold = new Regex(@"\x02");

		private static ILog logger = LogManager.GetLogger("CTFBot.SourceReader");

		public void initiateConnection()
		{
			Thread.CurrentThread.Name = "SourceReader";

			logger.Info("SourceReader thread started");

			//Set up SourceReader
			sourceirc.Encoding = System.Text.Encoding.UTF8;
			sourceirc.AutoReconnect = true;
			//sourceirc.AutoRejoin = true;
			sourceirc.OnChannelMessage += new IrcEventHandler(sourceirc_OnChannelMessage);
			sourceirc.OnConnected += new EventHandler(sourceirc_OnConnected);
			sourceirc.OnQueryMessage += new IrcEventHandler(sourceirc_OnQueryMessage);
			sourceirc.OnDisconnected += new EventHandler(sourceirc_OnDisconnected);
			sourceirc.OnConnectionError += new EventHandler(sourceirc_OnConnectionError);

			try
			{
				sourceirc.Connect(Program.sourceServerName, 6667);
			}
			catch (ConnectionException e)
			{
				logger.Fatal("Could not connect: " + e.Message);
				return;
			}
			try
			{
				sourceirc.Login(Program.botNick, "CTFBot", 4, "CTFBot");

				logger.Info("Joining source channel: " + Program.sourceChannel);
				sourceirc.RfcJoin(Program.sourceChannel);

				//Enter loop
				sourceirc.Listen();
				sourceirc.Disconnect();
			}
			catch (ConnectionException)
			{
				//Apparently this is handled, so we don't need to catch it
				return;
			}
		}

		void sourceirc_OnConnectionError(object sender, EventArgs e)
		{
			//Let's try to catch those awkward disposal exceptions
			//If it ain't a legitimate disconnection (i.e., if it wasn't ordered)
			if (sourceirc.AutoReconnect)
			{
				logger.Error("Caught connection error in SourceReader class, restarting...");
				Program.Restart();
			}
		}

		void sourceirc_OnDisconnected(object sender, EventArgs e)
		{
			if (sourceirc.AutoReconnect)
			{
				//Was an unexpected disconnection
				logger.Warn("SourceReader has been disconnected, attempting reconnection");
				sourceirc.Reconnect();
			}
		}

		void sourceirc_OnQueryMessage(object sender, IrcEventArgs e)
		{
			//This is for the emergency restarter
			if (e.Data.Message == Program.botNick + ":" + (string)Program.mainConfig["botpass"] + " restart")
			{
				logger.Warn("Emergency restart ordered by " + e.Data.Nick);
				Program.PartIRC("Emergency restart ordered by " + e.Data.Nick);
				Program.Restart();
			}
		}

		void sourceirc_OnConnected(object sender, EventArgs e)
		{
			logger.Info("Connected to source: " + Program.sourceServerName);
		}

		void sourceirc_OnChannelMessage(object sender, IrcEventArgs e)
		{
			lastMessage = DateTime.Now;

			/* Sample input
			:ClueBot_NG!~ClueBot_N@192.168.2.146 PRIVMSG #wikipedia-VAN :15[[07Title15]] by "0380.100.196.12815" (12 http://en.wikipedia.org/w/index.php?diff=403417897&oldid=396729660 15) 060.90746715 (04Reverted15) (13Default revert15) (021.234 15s)
			
			:ClueBot_NG!~ClueBot_N@192.168.2.146 PRIVMSG #wikipedia-VAN :15[[07Page15]] by "03127.0.0.115" (12 http://en.wikipedia.org/w/index.php?diff=403418859&oldid=403418512 15) 060.95910315 (03Not Reverted15) (13Reverted before15) (020.3157 15s)
			*/


			string strippedmsg = stripBold.Replace(stripColours.Replace(CTFUtils.replaceStrMax(e.Data.Message, '\x03', '\x04', 14), "\x03"), "");
			string[] fields = strippedmsg.Split(new char[1] { '\x03' }, 15);

			if (fields.Length == 15)
			{
				/* Sample fields
				0:
				1:[[
				2:Triangular trade
				3:]] by "
				4:173.57.160.195
				5:" (
				6: http://en.wikipedia.org/w/index.php?diff=403448965&oldid=402566006 
				7:) 
				8:0.935177
				9: (
				10:Reverted
				11:) (
				12:Default revert
				13:) (
				14:3.0718929767609 15s)
				--
				0:
				1:[[
				2:Heterosexuality
				3:]] by "
				4:68.194.43.126
				5:" (
				6: http://en.wikipedia.org/w/index.php?diff=403448989&oldid=403448396 
				7:) 
				8:0.963363
				9: (
				10:Not Reverted
				11:) (
				12:Default revert
				13:) (
				14:3.0718929767609 15s)
				*/
				
				// Cut off possible special character at the end
				if (fields[14].EndsWith("\x03"))
					fields[14] = fields[14].Substring(0, fields[14].Length - 1);

				// Cut off " s)" too
				string[] timeparts = fields[14].Split(new char[1] { ' ' }, 2);
				fields[14] = timeparts[0];
			}
			else
			{
				//Console.WriteLine("Ignored: " + e.Data.Message);
				return; //Probably really long article title or something that got cut off; we can't handle these
			}

			try
			{
				SourceEvent rce;

				rce.eventtype = SourceEvent.EventType.somethingelse;

				rce.fullstring = e.Data.Message;
				rce.sendname = e.Data.Nick;
		
				rce.title = "";
				rce.user = "";
				rce.url = "";
				rce.score = "";
				rce.reverted = "";
				rce.reason = "";
				rce.time = "";

				try
				{
					if (e.Data.Nick == Program.sourceAccount)
					{
						rce.eventtype = SourceEvent.EventType.cluereport;
		
						rce.title = fields[2];
						rce.user = fields[4];
						rce.url = fields[6].Trim();
						rce.score = fields[8];
						rce.reverted = fields[10];
						rce.reason = fields[12];
						rce.time = fields[14];
					}
					Program.ReactToSourceEvent(rce);
				}
				catch (Exception exce)
				{
					logger.Warn("ReactorException: " + exce.Message);
				}
			}
			catch (ArgumentOutOfRangeException eor)
			{
				//Broadcast this for Distributed Debugging
				logger.Warn("SourceR_AOORE: " + eor.Message);
			}
		}

	}
}