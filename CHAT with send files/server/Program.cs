using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MongoDB.Driver;
using MongoDB.Bson;

class Server
{

    private static ConcurrentDictionary<string, List<ClientHandler>> rooms = new ConcurrentDictionary<string, List<ClientHandler>>();
    private static TcpListener tcpListener;
    private static TcpListener tcpListenerFile;

    static void Main(string[] args)
    {
        Database.Init();
        //IPEndPoint ip = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8080);       //Stream messsage Endpoint
        //IPEndPoint ipFile = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8081);   //Stream file transfer Endpoint

        int port = 8080;
        int portFile = 8081;

        tcpListener = new TcpListener(IPAddress.Any,port);
        tcpListenerFile = new TcpListener(IPAddress.Any,portFile);


        tcpListener.Start();
        tcpListenerFile.Start();

        Console.WriteLine("Server started on message port 8080 and file transfer port 8081");

        new Thread(ListenForClients).Start(); // Thread for catching client
    }

    private static void ListenForClients()
    {
        while (true)
        {
            var newClient = tcpListener.AcceptTcpClient();
            var newClientFile = tcpListenerFile.AcceptTcpClient();
            Console.WriteLine("New client connected.");
            Task.Run(() => HandleClientComm(newClient, newClientFile));
        }
    }

    private static void HandleClientComm(object obj1,object obj2)
    {
        
        var tcpClient = (TcpClient)obj1; // TCP CLient
        var tcpClientFile =(TcpClient)obj2;
        //(TcpClient)obj now is node for message and file transfer at the same time

        var stream = tcpClient.GetStream(); //  Stream for client, message stream
        var streamFile = tcpClientFile.GetStream(); // Stream for file transfer

        //var streamFile = tcpListenerFile.AcceptTcpClient().GetStream(); // Stream for file transfer
        var clientHandler = new ClientHandler(tcpClient, stream, streamFile);

        try
        {
            var userName = AskForUserName(stream);
            var roomCode = AskForRoomCode(stream);

            if (!rooms.ContainsKey(roomCode))
                rooms[roomCode] = new List<ClientHandler>();

            clientHandler.UserName = userName;
            rooms[roomCode].Add(clientHandler);
            Console.WriteLine($"{userName} joined room {roomCode}");

            // Send message history
            Database.GetMessagesByRoomCode(roomCode).ForEach(msg =>
                SendMessage(clientHandler.Stream, $"{msg.Timestamp}: {msg.UserName}: {msg.Content}\r\n"));

            BroadcastMessage($"{userName} has joined the room.", roomCode, clientHandler);

            while (true)
            {
                var message = clientHandler.ReceiveMessage();
                if (string.IsNullOrEmpty(message)) break;

                if (message.StartsWith("SEND_FILE")) 
                {
                    HandleFileUpload(message, clientHandler, roomCode, userName);
                }
                else if (message.StartsWith("GET_FILE")) 
                {
                    HandleFileDownload(message, clientHandler, roomCode);
                }
                else
                {
                    BroadcastMessage($"{userName}: {message}", roomCode, clientHandler);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
        }
        finally
        {
            foreach (var room in rooms.Values)
                room.Remove(clientHandler);

            clientHandler.TcpClient.Close();
        }
    }

    private static void HandleFileUpload(string message, ClientHandler clientHandler, string roomCode, string userName)
    {
        var parts = message.Split('|');
        string fileName = parts[1];
        long fileSize = long.Parse(parts[2]);

        if (fileSize > 10 * 1024 * 1024)
        {
            SendMessage(clientHandler.FileStream, "File size exceeds 10MB limit. Upload rejected.\n"); //
            return;
        }

        string savePath = $"SendFromClient/{roomCode}/{fileName}";
        Directory.CreateDirectory(Path.GetDirectoryName(savePath));

        BroadcastMessage($"{userName} uploaded a file: {fileName}", roomCode, clientHandler);

        Database.SaveFileMetadata(new FileMetadata
        {
            RoomCode = roomCode,
            FileName = fileName,
            FilePath = savePath,
            Sender = userName,
            Timestamp = DateTime.Now
        });

        using (var fs = new FileStream(savePath, FileMode.Create))
        {
            byte[] buffer = new byte[4096];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = clientHandler.FileStream.Read(buffer, 0, buffer.Length)) > 0) //
            {
                totalBytesRead += bytesRead;
                fs.Write(buffer, 0, bytesRead);
                if (totalBytesRead >= fileSize) break;
            }
            fs.Flush();
            fs.Close();
        }
    }

    private static void HandleFileDownload(string message, ClientHandler clientHandler, string roomCode)
    {
        string fileName = message.Split('|')[1];
        var fileMeta = Database.GetFileMetadata(roomCode, fileName);

        if (fileMeta != null)
        {
            var filePath = fileMeta.FilePath;
            var originalFileName = Path.GetFileName(filePath);
            var fileSize = new FileInfo(filePath).Length;

            SendMessage(clientHandler.Stream, $"SAVE_FILE|{originalFileName}|{fileSize}");

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    clientHandler.FileStream.Write(buffer, 0, bytesRead); //
                fs.Flush();
                fs.Close();
            }

            Console.WriteLine($"File {originalFileName} sent.");
        }
        else
        {
            SendMessage(clientHandler.Stream, "ERROR|File not found.");
        }
    }

    private static string AskForUserName(NetworkStream stream)
    {
        SendMessage(stream, "Enter your username: ");
        return ReadFromStream(stream);
    }

    private static string AskForRoomCode(NetworkStream stream)
    {
        SendMessage(stream, "Enter room code: ");
        return ReadFromStream(stream);
    }

    private static string ReadFromStream(NetworkStream stream)
    {
        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
    }

    private static void SendMessage(NetworkStream stream, string message)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        stream.Write(messageBytes, 0, messageBytes.Length);
    }

    private static void BroadcastMessage(string message, string roomCode, ClientHandler sender)
    {
        if (!rooms.ContainsKey(roomCode)) return;

        Database.SaveMessage(roomCode, sender.UserName, message);

        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        foreach (var clientHandler in rooms[roomCode])
        {
            if (clientHandler != sender)
                clientHandler.Stream.Write(messageBytes, 0, messageBytes.Length);
        }
    }
}

public class ClientHandler
{
    public TcpClient TcpClient { get; private set; }  // store client
    public NetworkStream Stream { get; private set; } // set stream message for client
    public NetworkStream FileStream { get; set; }   // set stream file transfer for client
    public string UserName { get; set; }

    public ClientHandler(TcpClient tcpClient, NetworkStream stream, NetworkStream streamFile) // constructor for client
    {
        TcpClient = tcpClient;
        Stream = stream;
        FileStream = streamFile;
    }

    public string ReceiveMessage() // receive message from client
    {
        byte[] buffer = new byte[4096];
        int bytesRead = Stream.Read(buffer, 0, buffer.Length);
        return bytesRead == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, bytesRead);
    }
}
public class Database
{
    private static IMongoCollection<Room> roomsCollection;
    private static IMongoCollection<Message> messagesCollection;
    private static IMongoCollection<FileMetadata> filesCollection;

    public static void Init()
    {
        var client = new MongoClient("mongodb://192.168.1.16:27017");
        var database = client.GetDatabase("chatapp");
        roomsCollection = database.GetCollection<Room>("rooms");
        messagesCollection = database.GetCollection<Message>("messages");
        filesCollection = database.GetCollection<FileMetadata>("files");
    }

    public static void SaveMessage(string roomCode, string userName, string messageContent)
    {
        var message = new Message
        {
            RoomCode = roomCode,
            UserName = userName,
            Content = messageContent,
            Timestamp = DateTime.Now
        };
        messagesCollection.InsertOne(message);
    }

    public static List<Message> GetMessagesByRoomCode(string roomCode) =>
        messagesCollection.Find(m => m.RoomCode == roomCode).ToList();

    public static void SaveFileMetadata(FileMetadata fileMetadata) =>
        filesCollection.InsertOne(fileMetadata);

    public static FileMetadata GetFileMetadata(string roomCode, string fileName) =>
        filesCollection.Find(f => f.RoomCode == roomCode && f.FileName == fileName).FirstOrDefault();
}

public class FileMetadata
{
    public ObjectId Id { get; set; }
    public string RoomCode { get; set; }
    public string FileName { get; set; }
    public string FilePath { get; set; }
    public string Sender { get; set; }
    public DateTime Timestamp { get; set; }
}

public class Message
{
    public ObjectId Id { get; set; }
    public string RoomCode { get; set; }
    public string UserName { get; set; }
    public string Content { get; set; }
    public DateTime Timestamp { get; set; }
}
public class Room
{
    public ObjectId Id { get; set; }
    public string RoomCode { get; set; }
    public DateTime CreatedAt { get; set; }
}
