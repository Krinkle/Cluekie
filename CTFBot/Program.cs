using System;
using System.Collections;
using Meebey.SmartIrc4net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Data;
using log4net;

//Logging:
[assembly: log4net.Config.XmlConfigurator(Watch = true)]

namespace CTFBot
{
	class QueuedMessage
	{
		public SendType type;
		public String destination;
		public String message;
		public long SentTime;
		public bool IsDroppable = false;
	}

	class Program
	{
		const string version = "1.0.9";

		public static IrcClient irc = new IrcClient();
		public static SourceReader sourceirc = new SourceReader();
		public static SortedList msgs = new SortedList();
		public static SortedList mainConfig = new SortedList();
		private static ILog logger = LogManager.GetLogger("CTFBot.Program");

		//Flood protection objects
		static Queue fcQueue = new Queue();
		static Queue priQueue = new Queue();
		static Boolean dontSendNow = false;
		static int sentLength = 0;
		static ManualResetEvent sendlock = new ManualResetEvent(true);

		static Regex botCmd;

		static string targetServerName;
		static string targetFeedChannel;
		static string targetCVNChannel;
		static string targetControlChannel;
		static string targetHomeChannel;
		static int bufflen = 1400;
		static long maxlag = 600000000; // 60 seconds in 100-nanoseconds

		public static string botNick;
		public static string sourceServerName;
		public static string sourceChannel;
		public static string sourceAccount;
		public static string targetWikiproject;
		public static string targetBlacklistDuration;

		static void Main(string[] args)
		{
			Thread.CurrentThread.Name = "Main";
			Thread.GetDomain().UnhandledException += new UnhandledExceptionEventHandler(Application_UnhandledException);

			string mainConfigFN = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
				+ Path.DirectorySeparatorChar + "CTFBot.ini";

			logger.Info("Loading main configuration from "+mainConfigFN);
			using (StreamReader sr = new StreamReader(mainConfigFN))
			{
				String line;
				while ((line = sr.ReadLine()) != null)
				{
					if (!line.StartsWith("#") && (line != "")) //ignore comments
					{
						string[] parts = line.Split(new char[1] { '=' }, 2);
						mainConfig.Add(parts[0], parts[1]);
					}
				}
			}

			botNick = (string)mainConfig["botnick"];
			sourceServerName = (string)mainConfig["sourceIrcserver"];
			sourceChannel = (string)mainConfig["sourceChannel"];
			sourceAccount = (string)mainConfig["sourceAccount"];
			targetServerName = (string)mainConfig["targetIrcserver"];
			targetFeedChannel = (string)mainConfig["targetFeedChannel"];
			targetCVNChannel = (string)mainConfig["targetCVNChannel"];
			targetControlChannel = (string)mainConfig["targetControlChannel"];
			targetHomeChannel = (string)mainConfig["targetHomeChannel"];
			// ClueNet only monitors en.wikipedia and there are no plans for monitoring more
			// and/or to split up in tracable sources. This only affects the links generated
			// and the Autoblacklist-message
			targetWikiproject = "en.wikipedia";
			targetBlacklistDuration = (string)mainConfig["targetBlacklistDuration"];

			botCmd = new Regex("^" + botNick + @" (\s*(?<command>\S*))(\s(?<params>.*))?$", RegexOptions.IgnoreCase);

			logger.Info("Loading messages");
			readMessages((string)mainConfig["messages"]);
			if ((!msgs.ContainsKey("00000")) || ((String)msgs["00000"] != "0.02"))
			{
				logger.Fatal("Message file version mismatch or read messages failed");
				Exit();
			}

			logger.Info("Setting up main IRC client");
			//Set up freenode IRC client
			irc.Encoding = System.Text.Encoding.UTF8;
			irc.SendDelay = 300;
			//irc.AutoReconnect = true;
			//irc.AutoRejoin = true;
			irc.ActiveChannelSyncing = true;
			irc.OnChannelMessage += new IrcEventHandler(irc_OnChannelMessage);
			irc.OnChannelNotice += new IrcEventHandler(irc_OnChannelNotice);
			irc.OnConnected += new EventHandler(irc_OnConnected);
			irc.OnError += new Meebey.SmartIrc4net.ErrorEventHandler(irc_OnError);
			irc.OnConnectionError += new EventHandler(irc_OnConnectionError);
			irc.OnPong += new PongEventHandler(irc_OnPong);
			//irc.PingTimeout = 10;

			try
			{
				irc.Connect(targetServerName, 6667);
			}
			catch (ConnectionException e)
			{
				logger.Fatal("Could not connect: " + e.Message);
				Exit();
			}

			// Now initialize flood protection code
			new Thread(new ThreadStart(msgthread)).Start();

			try
			{
				irc.Login(botNick, (string)mainConfig["description"] + " " + version, 4, botNick, (string)mainConfig["botpass"]);
                if (targetFeedChannel != "None")
                {
					logger.Info("Joining feed channel: " + targetFeedChannel);
					irc.RfcJoin(targetFeedChannel);
				}
				if (targetCVNChannel != "None")
                {
					logger.Info("Joining cvn channel: " + targetCVNChannel);
					irc.RfcJoin(targetCVNChannel);
				}
				if (targetControlChannel != "None")
                {
					logger.Info("Joining control channel: " + targetControlChannel);
					irc.RfcJoin(targetControlChannel);
				}
				if (targetHomeChannel != "None")
                {
					logger.Info("Joining home channel: " + targetHomeChannel);
					irc.RfcJoin(targetHomeChannel);
				}

				//Now connect the SourceReader to channels
				new Thread(new ThreadStart(sourceirc.initiateConnection)).Start();

				// here we tell the IRC API to go into a receive mode, all events
				// will be triggered by _this_ thread (main thread in this case)
				// Listen() blocks by default, you can also use ListenOnce() if you
				// need that does one IRC operation and then returns, so you need then
				// an own loop
				irc.Listen();

				// when Listen() returns our IRC session is over, to be sure we call
				// disconnect manually
				irc.Disconnect();
			}
			catch (ConnectionException)
			{
				// this exception is handled because Disconnect() can throw a not
				// connected exception
				Exit();
			}
			catch (Exception e)
			{
				// this should not happen by just in case we handle it nicely
				logger.Fatal("Error occurred in Main IRC try clause! Message: " + e.Message);
				logger.Fatal("Exception: " + e.StackTrace);
				Exit();
			}
		}

		static void irc_OnConnectionError(object sender, EventArgs e)
		{
			//Let's try to catch those strange disposal errors
			//But only if it ain't a legitimate disconnection
			if (sourceirc.sourceirc.AutoReconnect)
			{
				logger.Error("OnConnectionError in Program, restarting...");
				Restart();
				//Exit(); /* DEBUG */
			}
		}

		/// <summary>
		/// Catches all unhandled exceptions in the main thread
		/// </summary>
		public static void Application_UnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			try
			{
				logger.Error("Caught unhandled exception in global catcher", (Exception)e.ExceptionObject);
			}
			catch
			{
				//Logging failed; considerably serious
				Console.WriteLine("Caught unhandled exception, and logging failed: " + ((Exception)e.ExceptionObject).ToString());

				try
				{
					PartIRC("Caught unhandled exception and logging failed; restarting as a precaution");
					Restart();
				}
				catch
				{
					//Restart failed
					Console.WriteLine("Restart failed; exiting with code 24.");
					System.Environment.Exit(24);
				}
			}
		}

		static void irc_OnError(object sender, Meebey.SmartIrc4net.ErrorEventArgs e)
		{
			logger.Error("IRC: " + e.ErrorMessage);
			if (e.ErrorMessage.Contains("Excess Flood")) //Do not localize
			{
				//Oops, we were flooded off
				logger.Warn("Initiating restart sequence after Excess Flood");
				Restart();
			}
		}

		/// <summary>
		/// This event handler detects incoming notices
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		static void irc_OnChannelNotice(object sender, IrcEventArgs e)
		{

		}

		static void irc_OnConnected(object sender, EventArgs e)
		{
			logger.Info("Connected to target: " + targetServerName);
		}


		#region Flood protection code

		/// <summary>
		/// Route all irc.SendMessage() calls through this to use the queue
		/// </summary>
		public static void SendMessageF(SendType type, string destination, string message, bool IsDroppable, bool IsPriority)
		{
			QueuedMessage qm = new QueuedMessage();
			qm.type = type;
			qm.message = message;
			qm.destination = destination;
			qm.SentTime = DateTime.Now.Ticks;
			qm.IsDroppable = IsDroppable;

			if (IsPriority)
				lock (priQueue)
					priQueue.Enqueue(qm);
			else
				lock (fcQueue)
					fcQueue.Enqueue(qm);

			//logger.Info("Queued item");
		}

		/// <summary>
		/// Splitting messages by line breaks and in chucks if they're too long and forward to SendMessageF
		/// </summary>
		public static void SendMessageFMulti(SendType type, string destination, string message, bool IsDroppable, bool IsPriority)
		{

			if (message != "")
			{
				//Allow multiline
				foreach (string line in message.Split(new char[1] { '\n' }))
				{
					//Chunk messages that are too long
					foreach (string chunk in CTFUtils.stringSplit(line, 400))
					{
						// Ignore "" and "
						if ((chunk.Trim() != "\"\"") && (chunk.Trim() != "\"")){
							SendMessageF(type, destination, chunk, IsDroppable, IsPriority);
						}
					}
				}
			}
		}


		static void irc_OnPong(object sender, PongEventArgs e)
		{
			sentLength = 0;
			dontSendNow = false;
			sendlock.Set();
			//logger.Info("Got pong: " + e.Data.RawMessage);
			irc.LastPongReceived = DateTime.Now; //Hacked SmartIrc4net
		}

		/// <summary>
		/// Calculates the rough length, in bytes, of a queued message
		/// </summary>
		/// <param name="qm"></param>
		/// <returns></returns>
		static int calculateByteLength(QueuedMessage qm)
		{
			// PRIVMSG #channelname :My message here (10 + destination + message)
			// NOTICE #channelname :My message here (9 + dest + msg)
			if (qm.type == SendType.Notice)
				return 11 + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.message)
					+ System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.destination);
			else
				return 12 + System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.message)
					+ System.Text.ASCIIEncoding.Unicode.GetByteCount(qm.destination);
		}

		/// <summary>
		/// Thread function that runs continuously in the background, sending messages
		/// </summary>
		static void msgthread()
		{
			Thread.CurrentThread.Name = "Messaging";
			Thread.CurrentThread.IsBackground = true; //Allow runtime to close this thread at shutdown
			//Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
			logger.Info("Started messaging");

			while (irc.IsConnected) {
				QueuedMessage qm;

				//Console.WriteLine("Lag is " + irc.Lag.ToString());

				// First check for any priority messages to send
				if (priQueue.Count > 0)
				{
					// We have priority messages to handle
					lock (priQueue)
						qm = (QueuedMessage)priQueue.Dequeue();
				}
				else
				{
					// No priority messages; let's handle the regular-class messages
					// Do we have any messages to handle?
					if (fcQueue.Count == 0)
					{
						// No messages at all to handle
						Thread.Sleep(50); // Sleep for 50 miliseconds
						continue; // Start the loop over
					}

					// We do have a message to dequeue, so dequeue it
					lock (fcQueue)
						qm = (QueuedMessage)fcQueue.Dequeue();

					//logger.Info(fcQueue.Count.ToString() + " in normal. sentLength: " + sentLength.ToString());
				}


				// Okay, we now have a message to handle in qm

				// Is our message too old?
				if (qm.IsDroppable && (DateTime.Now.Ticks - qm.SentTime > maxlag))
				{
					//logger.Info("Lost packet");
					continue; // Start the loop over
				}

				// If it's okay to send now, but we would exceed the bufflen if we were to send it
				if (!dontSendNow && (sentLength + calculateByteLength(qm) + 2 > bufflen))
				{
					// Ping the server and wait for a reply
					irc.RfcPing(targetServerName); //Removed Priority.Critical
					irc.LastPingSent = DateTime.Now; //Hacked SmartIrc4net
					sendlock.Reset();
					dontSendNow = true;
					//logger.Info("Waiting for artificial PONG");
				}

				// Sleep while it's not okay to send
				while (dontSendNow)
					Thread.Sleep(1000);
				//sendlock.WaitOne();

				// Okay, we can carry on now. Is our message still fresh?
				if (qm.IsDroppable && (DateTime.Now.Ticks - qm.SentTime > maxlag))
				// Oops, sowwy. Our message has rotten.
				{
					//logger.Info("Lost packet");
					continue; // Start the loop over
				}

				// At last! Send the damn thing!
				// ...but only if we're still connected
				if (irc.IsConnected)
				{
					sentLength = sentLength + calculateByteLength(qm) + 2;
					irc.SendMessage(qm.type, qm.destination, qm.message);
				}

				//logger.Info("Lag was " + (DateTime.Now.Ticks - qm.SentTime));

				// Throttle on our part
				Thread.Sleep(300);
			}

			logger.Info("Thread ended");
		}

		#endregion

		static bool hasPrivileges(char minimum, ref IrcEventArgs e)
		{
			switch (minimum)
			{
				case '@':
					if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp)
					{
						SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00102"], false, true);
						return false;
					}
					else
						return true;
				case '+':
					if (!irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsOp && !irc.GetChannelUser(e.Data.Channel, e.Data.Nick).IsVoice)
					{
						SendMessageF(SendType.Notice, e.Data.Nick, (String)msgs["00100"], false, true);
						return false;
					}
					else
						return true;
				default:
					return false;
			}
		}

		static void irc_OnChannelMessage(object sender, IrcEventArgs e)
		{
			if (e.Data.Message == "" || e.Data.Message == null)
				return; //Prevent empty messages from crashing the bot

			Match cmdMatch = botCmd.Match(e.Data.Message);

			if (cmdMatch.Success)
			{

				 // Have to be voiced to issue commands at all
				 if (!hasPrivileges('+', ref e))
					 return;
	
				 string command = cmdMatch.Groups["command"].Captures[0].Value;
	
				 switch (command)
				 {
					 case "quit":
						 if (!hasPrivileges('@', ref e))
							 return;
						 logger.Info(e.Data.Nick + " ordered a quit");
						 PartIRC((string)mainConfig["partmsg"]);
						 Exit();
						 break;
					 case "restart":
						 if (!hasPrivileges('@', ref e))
							 return;
						 logger.Info(e.Data.Nick + " ordered a restart");
						 PartIRC("Rebooting by order of " + e.Data.Nick + " ...");
						 Restart();
						 break;
					 case "status":
						 TimeSpan ago = DateTime.Now.Subtract(sourceirc.lastMessage);
						 SendMessageF(SendType.Message, e.Data.Channel, "Last message was received on SourceReader "
							 + ago.TotalSeconds + " seconds ago", false, false);
						 break;
					 case "help":
						 SendMessageF(SendType.Message, e.Data.Channel, (String)msgs["20005"], false, true);
						 break;
					 case "config":
					 case "settings":
					 case "version":
						 BotConfigMsg(e.Data.Channel);
						 break;
					 case "msgs":
						 //Reloads msgs
						 if (!hasPrivileges('@', ref e))
							 return;
						 readMessages((string)mainConfig["messages"]);
						 SendMessageF(SendType.Message, e.Data.Channel, "Re-read messages", false, false);
						 break;
				 }
			 }
		}

		/// <summary>
		/// Reads messages from filename (Console.msgs) into SortedList msgs
		/// </summary>
		/// <param name="filename">File to read messages from</param>
		static void readMessages(string filename)
		{
			msgs.Clear();
			try
			{
				using (StreamReader sr = new StreamReader(filename))
				{
					String line;
					while ((line = sr.ReadLine()) != null)
					{
						if (line.StartsWith("#") || (line == ""))
						{
							//Ignore: comment or blank line
						}
						else
						{
							string[] parts = line.Split(new char[1] { '=' }, 2);
							msgs.Add(parts[0], parts[1].Replace(@"%c", "\x03").Replace(@"%b", "\x02"));
						}
					}
				}
			}
			catch (Exception e)
			{
				logger.Error("Unable to read messages from file", e);
			}
		}

		/// <summary>
		/// Gets a message from the msgs store
		/// </summary>
		/// <param name="msgCode">The five-digit message code</param>
		/// <param name="attributes">The attributes to place in the message</param>
		/// <returns></returns>
		static string getMessage(int msgCode, ref Hashtable attributes)
		{
			try
			{
				string message = (string)msgs[msgCode.ToString().PadLeft(5,'0')];
				foreach (DictionaryEntry de in attributes)
				{
					message = message.Replace("${" + (string)de.Key + "}", (string)de.Value);
				}
				return message;
			}
			catch (Exception e)
			{
				logger.Error("Cannot getMessage", e);
				return "[Error: cannot get message]";
			}
		}

		/// <summary>
		/// Get a message from the msgs store, and format it using the parameters specified.
		/// Messages should be those with a "1" prefix, incompatible with CVUBot.
		/// </summary>
		/// <param name="msgCode">The five-digit message code</param>
		/// <param name="fParams">The parameters to place in the string</param>
		/// <returns></returns>
		public static string getFormatMessage(int msgCode, params String[] fParams) {
			try
			{
				string message = (string)msgs[msgCode.ToString().PadLeft(5, '0')];
				return String.Format(message, fParams);
			}
			catch (Exception e)
			{
				logger.Error("Cannot getFormatMessage " + msgCode.ToString(), e);
				return "[Error: cannot get message]";
			}
		}

		/// <summary>
		/// Reacts to the Source Event, passed from SourceReader. Remember: this runs in the SourceReader thread!
		/// </summary>
		/// <param name="r"></param>
		public static void ReactToSourceEvent(SourceEvent r)
		{

			Hashtable attribs = new Hashtable();
			String message = "";

			switch (r.eventtype)
			{
				case SourceEvent.EventType.cluereport:
					attribs.Add("line", r.fullstring);
					attribs.Add("title", r.title);
					attribs.Add("user", r.user);
					attribs.Add("listduration", targetBlacklistDuration);
					attribs.Add("listreason", "Autoblacklist: " + r.reason + " by [[User:ClueBot_NG|ClueBot NG]] on [[" + r.title + "]] at " + targetWikiproject);
					attribs.Add("reason", r.reason);
					attribs.Add("diff", r.url.Replace("/w/index.php?diff=", "/?diff="));
					attribs.Add("history", "http://" + targetWikiproject + ".org/?title=" + CTFUtils.wikiEncode(r.title) + "&action=history");
					attribs.Add("contribs", "http://" + targetWikiproject + ".org/wiki/Special:Contributions/" + CTFUtils.wikiEncode(r.user));

					// Feed the feedchannel
					message = getMessage(10100, ref attribs);
					SendMessageFMulti(SendType.Message, targetFeedChannel, message, false, true);

						// If it was reverted by ClueBot, blacklist it
						if (r.reverted == "Reverted")
						{
							message = getMessage(20001, ref attribs);
							SendMessageFMulti(SendType.Message, targetCVNChannel, message, false, true);
						} else {
							// Else send a warning
								// TODO: Some "Not Reverted" events shouldn't be
								// relayed to #cvn-wp-en as "Possible ignored vandalism"
								// namely these:
								// reason: "User is myself"
								// reason: "Beaten by *"
							if (r.reason == "User is myself" || r.reason.Contains("Beaten by "))
							{
								// We don't report if the
								// * edit was not reverted by the bot, but by someone else
								// * edit was not reverted by the bot, and made by the bot
							} else {
								message = getMessage(20002, ref attribs);
								message = message + "\n" + getMessage(20003, ref attribs);
								SendMessageFMulti(SendType.Message, targetCVNChannel, message, false, true);
							}
						}
					break;
				case SourceEvent.EventType.somethingelse:
					attribs.Add("line", r.fullstring);
					/// Dont report other stuff
					message = "";// getMessage(10101, ref attribs);
					break;
			}

		}

		public static void BotConfigMsg(string destChannel)
		{

			string settingsmessage = "runs version: " + version
			+ "; feedChannel: " + targetFeedChannel
			+ "; CVNChannel: " + targetCVNChannel;

			SendMessageFMulti(SendType.Action, destChannel, settingsmessage, false, true);

		}

		public static void Exit()
		{
			try
			{
				//Delayed quitting after parting in PartIRC()
				irc.Disconnect();
				sourceirc.sourceirc.AutoReconnect = false;
				sourceirc.sourceirc.Disconnect();

				LogManager.Shutdown();
			}
			catch
			{
				//Ignore
			}
			finally
			{
				System.Environment.Exit(0);
			}
		}


		public static void Restart()
		{
			//If a custom restartcmd / restartarg has been set in the main config, use that
			if (mainConfig.ContainsKey("restartcmd"))
			{
				//Execute the custom command
				System.Diagnostics.Process.Start((string)mainConfig["restartcmd"], (string)mainConfig["restartarg"]);
			}
			else
			{
				//Note: argument is not actually used, but it's there to prevent a mono bug
				System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart");
			}
			Exit();
		}

		public static void PartIRC(string quitMessage)
		{
			sourceirc.sourceirc.AutoReconnect = false;
			sourceirc.sourceirc.RfcQuit(quitMessage);
			irc.RfcPart(targetFeedChannel, quitMessage);
			irc.RfcPart(targetCVNChannel, quitMessage);
			Thread.Sleep(1000);
		}
	}
}