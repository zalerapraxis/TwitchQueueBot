using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchLib;
using TwitchLib.Client;
using TwitchLib.Client.Enums;
using TwitchLib.Client.Events;
using TwitchLib.Client.Extensions;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;
using Flurl.Http;


namespace TwitchQueueBot
{
    class Program
    {
        static void Main(string[] args)
        {
            Bot bot = new Bot();
            Console.ReadLine();
        }
    }

    class Bot
    {
        public string BotUsername = ConfigurationManager.AppSettings["BotUsername"];
        public string BotAccessToken = ConfigurationManager.AppSettings["BotAccessToken"];
        public string ChannelToJoin = ConfigurationManager.AppSettings["ChannelToJoin"];
        public string ChatCommandPrefix = ConfigurationManager.AppSettings["ChatCommandPrefix"];

        public List<string> UsersInQueue = new List<string>();
        public List<string> UsersInChat = new List<string>();
        public List<string> ModeratorsInChat = new List<string>();
        public bool IsQueueOpen = false;

        TwitchClient client;

        public Bot()
        {
            ConnectionCredentials credentials = new ConnectionCredentials(BotUsername, BotAccessToken);
            var clientOptions = new ClientOptions
            {
                MessagesAllowedInPeriod = 750,
                ThrottlingPeriod = TimeSpan.FromSeconds(30)
            };
            WebSocketClient customClient = new WebSocketClient(clientOptions);
            client = new TwitchClient(customClient);
            client.Initialize(credentials, ChannelToJoin);

            client.OnLog += Client_OnLog;
            client.OnJoinedChannel += Client_OnJoinedChannel;
            client.OnConnected += Client_OnConnected;
            client.OnMessageReceived += Client_OnMessageReceived;

            client.Connect();
        }

        private void Client_OnLog(object sender, OnLogArgs e)
        {
            Console.WriteLine($"{e.DateTime.ToString()}: {e.BotUsername} - {e.Data}");
        }

        private void Client_OnConnected(object sender, OnConnectedArgs e)
        {
            Console.WriteLine($"Connected to {e.AutoJoinChannel}");
        }

        private void Client_OnJoinedChannel(object sender, OnJoinedChannelArgs e)
        {
            //Console.WriteLine($"Connected to {e.Channel}");
        }

        private void Client_OnMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            // check if message was a command
            if (HasStringPrefix(e.ChatMessage.Message))
            {
                ProcessUserCommand(e);
                return; // don't try to process anything else
            }
            
        }


        private bool HasStringPrefix(string message)
        {
            if (!string.IsNullOrEmpty(message) && message.StartsWith(ChatCommandPrefix, StringComparison.Ordinal))
                return true;
            return false;
        }

        private async Task<List<string>> GetUsersInChat()
        {
            var usersCurrentlyInChat = new List<string>();
            var url = $"https://tmi.twitch.tv/group/user/{ChannelToJoin}/chatters";
            var result = await url.GetJsonAsync<ChattersInfo>();

            usersCurrentlyInChat.AddRange(result.chatters.viewers);
            usersCurrentlyInChat.AddRange(result.chatters.vips);
            usersCurrentlyInChat.AddRange(result.chatters.moderators);
            usersCurrentlyInChat.AddRange(result.chatters.global_mods);
            usersCurrentlyInChat.AddRange(result.chatters.staff);
            usersCurrentlyInChat.AddRange(result.chatters.admins);

            return usersCurrentlyInChat;
        }

        private async Task<List<string>> GetModsInChat()
        {
            var modsCurrentlyInChat = new List<string>();
            var url = $"https://tmi.twitch.tv/group/user/{ChannelToJoin}/chatters";
            var result = await url.GetJsonAsync<ChattersInfo>();

            modsCurrentlyInChat.AddRange(result.chatters.broadcaster);
            modsCurrentlyInChat.AddRange(result.chatters.moderators);

            return modsCurrentlyInChat;
        }

        private bool CheckIfUserIsMod(CommandUserInfo user)
        {
            if (ModeratorsInChat.Find(x => x.Equals(user.userName)) != null)
                return true;
            return false;
        }

        private bool IsUserInQueue(string displayname)
        {
            return UsersInQueue.Contains(displayname);
        }

        private int GetUserPositionInQueue(string displayname)
        {
            return UsersInQueue.FindIndex(x => x.Contains(displayname));
        }

        private async void ProcessUserCommand(OnMessageReceivedArgs e)
        {
            UsersInChat = await GetUsersInChat();
            ModeratorsInChat = await GetModsInChat();


            // get information about the user
            var user = new CommandUserInfo();
            user.displayName = e.ChatMessage.DisplayName;
            user.userIsInQueue = IsUserInQueue(user.displayName);
            user.userName = e.ChatMessage.Username;
            if (user.userIsInQueue)
                user.userPositionInQueue = GetUserPositionInQueue(user.displayName);



            // command add test users
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}test"))
                ProcessCommandTest(e);

            // command join queue
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}join"))
                ProcessCommandJoinQueue(e, user);

            // command leave queue
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}leave"))
                ProcessCommandLeaveQueue(e, user);

            // command check position
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}pos"))
                ProcessCommandGetPosition(e, user);

            // command view queued users
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}queue") || e.ChatMessage.Message.Contains($"{ChatCommandPrefix}q"))
                ProcessCommandQueueList(e, user);

            // command select next queued user
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}next"))
                ProcessCommandNext(e, user);

            // command open queue - allows ppl to join queue
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}open"))
                ProcessCommandOpenQueue(e, user);

            // command close queue
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}close"))
                ProcessCommandCloseQueue(e, user);

            // command clear queue
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}clear"))
                ProcessCommandClearQueue(e, user);

            // command show help
            if (e.ChatMessage.Message.Equals($"{ChatCommandPrefix}help"))
                ProcessCommandHelp(e);

            // command add user to queue
            if (e.ChatMessage.Message.Contains($"{ChatCommandPrefix}add"))
                ProcessCommandAddUserToQueue(e, user);
        }

        /// <summary>
        /// Adds user to queue, if queue is enabled
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandJoinQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // only allow joins if the queue is enabled
            if (!IsQueueOpen)
            {
                client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, the queue isn't open (yet)!");
                return;
            }

            // check if user is already in queue
            if (user.userIsInQueue)
            {
                client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, you're already in the queue at position {user.userPositionInQueue}!");
                return;
            }

            // add to queue logic
            UsersInQueue.Add(user.displayName);
            client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, you've been added to the queue!");

            return;
        }

        /// <summary>
        /// Removes user from queue
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandLeaveQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if user is in the queue logic
            if (!user.userIsInQueue)
            {
                client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, you're not in the queue!");
                return;
            }

            // remove from queue logic
            UsersInQueue.RemoveAt(user.userPositionInQueue);
            client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, you've been removed from the queue!");

            return;
        }

        /// <summary>
        /// Checks user position in queue
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandGetPosition(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if user is in the queue logic
            if (!user.userIsInQueue)
            {
                client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, you're not in the queue!");
                return;
            }

            // check user position in queue
            client.SendMessage(e.ChatMessage.Channel, $"Hey {e.ChatMessage.DisplayName}, your place in queue is: {user.userPositionInQueue}.");

            return;
        }

        /// <summary>
        /// Shows everyone in queue
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandQueueList(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if anyone's in the queue
            if (UsersInQueue.Count <= 0)
            {
                client.SendMessage(e.ChatMessage.Channel, $"There are currently no users in the queue.");
                return;
            }

            // get the users in queue, build them into a pretty list
            StringBuilder formattedListOfUsers = new StringBuilder();
            var lastUserInList = UsersInQueue[UsersInQueue.Count - 1];
            foreach (var queuedUser in UsersInQueue)
            {
                formattedListOfUsers.Append(queuedUser);
                if (queuedUser != lastUserInList)
                    formattedListOfUsers.Append(", ");
            }

            client.SendMessage(e.ChatMessage.Channel, $"The following users are in queue: {formattedListOfUsers}.");

            return;
        }

        /// <summary>
        /// (Mod only) selects next user in queue
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandNext(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if the user has permissions to perform this command
            var isUserMod = CheckIfUserIsMod(user);
            if (!isUserMod)
            {
                client.SendMessage(e.ChatMessage.Channel, $"You don't have permission to use that.");
                return;
            }

            // check if anyone's in the queue
            if (UsersInQueue.Count <= 0)
            {
                client.SendMessage(e.ChatMessage.Channel, $"There are currently no users in the queue.");
                return;
            }

            // check if the user is still online
            bool lookingForNextQueuedUser = true;
            while (lookingForNextQueuedUser)
            {
                var username = UsersInQueue[0].ToLower();
                if (UsersInChat.Find(x => x.Equals(username)) != null)
                {
                    // this user is online, stop looking
                    lookingForNextQueuedUser = false;
                    break;
                }

                // otherwise, remove the user from the queue, sucks to suck
                Console.WriteLine($"{UsersInQueue[0]} was removed due to being offline.");
                UsersInQueue.RemoveAt(0);
            }

            // let the user know they're next in line, and then remove them from the queue
            client.SendMessage(e.ChatMessage.Channel, $"Hey {UsersInQueue[0]}, it's your turn!");
            UsersInQueue.RemoveAt(0);
            return;
        }

        /// <summary>
        /// (Mod only) Opens the queue & enables the join command
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandOpenQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if the user has permissions to perform this command
            var isUserMod = CheckIfUserIsMod(user);
            if (!isUserMod)
            {
                client.SendMessage(e.ChatMessage.Channel, $"You don't have permission to use that.");
                return;
            }

            // check if queue is open
            if (IsQueueOpen)
            {
                client.SendMessage(e.ChatMessage.Channel, "The queue is already open.");
                return;
            }

            // enable the queue and inform users
            IsQueueOpen = true;
            client.SendMessage(e.ChatMessage.Channel, $"The queue has been opened! Use {ChatCommandPrefix}join to enter!");
            return;
        }

        /// <summary>
        /// (Mod only) Closes the queue & disables the join command
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandCloseQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if the user has permissions to perform this command
            var isUserMod = CheckIfUserIsMod(user);
            if (!isUserMod)
            {
                client.SendMessage(e.ChatMessage.Channel, $"You don't have permission to use that.");
                return;
            }

            // check if queue is open
            if (!IsQueueOpen)
            {
                client.SendMessage(e.ChatMessage.Channel, "The queue is already closed.");
                return;
            }

            // enable the queue and inform users
            IsQueueOpen = false;
            client.SendMessage(e.ChatMessage.Channel, $"The queue has been closed.");
            return;
        }

        /// <summary>
        /// (Mod only) Clears the queue out
        /// </summary>
        /// <param name="e"></param>
        /// <param name="user"></param>
        private void ProcessCommandClearQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if the user has permissions to perform this command
            var isUserMod = CheckIfUserIsMod(user);
            if (!isUserMod)
            {
                client.SendMessage(e.ChatMessage.Channel, $"You don't have permission to use that.");
                return;
            }

            // clear the queue, inform user
            UsersInQueue.Clear();
            client.SendMessage(e.ChatMessage.Channel, $"The queue has been cleared.");
            return;
        }

        private void ProcessCommandAddUserToQueue(OnMessageReceivedArgs e, CommandUserInfo user)
        {
            // check if the user has permissions to perform this command
            var isUserMod = CheckIfUserIsMod(user);
            if (!isUserMod)
            {
                client.SendMessage(e.ChatMessage.Channel, $"You don't have permission to use that.");
                return;
            }

            var parsedMessage = e.ChatMessage.Message.Split(' ');
            var username = parsedMessage[1];
            var positionInQueueParsedSuccessfully = int.TryParse(parsedMessage[2], out int positionInQueue);

            if (!positionInQueueParsedSuccessfully)
            {
                client.SendMessage(e.ChatMessage.Channel, $"Incorrect format. Format is {ChatCommandPrefix}add username index.");
                return;
            }

            UsersInQueue.Insert(positionInQueue, username);
            client.SendMessage(e.ChatMessage.Channel, $"User {username} added to queue at position {positionInQueue}.");
        }

        private void ProcessCommandHelp(OnMessageReceivedArgs e)
        {
            client.SendMessage(e.ChatMessage.Channel, $"Here are the commands:{Environment.NewLine}" +
                $"!join - join queue {Environment.NewLine}" +
                $"!leave - leave queue {Environment.NewLine}" +
                $"!pos - check position in queue {Environment.NewLine}" +
                $"!queue - view viewer in queue {Environment.NewLine}" +
                $"!next - select next viewer. Mods only!");

            return;
        }

        /// <summary>
        /// Adds fake users to the queue
        /// </summary>
        /// <param name="e"></param>
        private void ProcessCommandTest(OnMessageReceivedArgs e)
        {
            var wot = new Random();
            var i = 0;
            while (i < 10)
            {
                UsersInQueue.Add($"User{wot.Next(99999)}");
                i++;
            }

            client.SendMessage(e.ChatMessage.Channel, $"[TEST] Fake users have been added to the queue.");

            return;
        }
    }

    // chatters object for twitch chatters request
    public class Chatters
    {
        public List<string> broadcaster { get; set; }
        public List<string> vips { get; set; }
        public List<string> moderators { get; set; }
        public List<string> staff { get; set; }
        public List<string> admins { get; set; }
        public List<string> global_mods { get; set; }
        public List<string> viewers { get; set; }
    }

    // root object for twitch chatters request
    public class ChattersInfo
    {
        public int chatter_count { get; set; }
        public Chatters chatters { get; set; }
    }

    // object containing user data for who calls a command
    public class CommandUserInfo
    {
        public string userName { get; set; }
        public string displayName { get; set; }
        public int userPositionInQueue { get; set; } = 0;
        public bool userIsInQueue { get; set; }
    }
}
