using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MongoDB.Driver;
using MongoDB.Bson;
using SharpCompress.Common;

class Server
{
    private static ConcurrentDictionary<string, List<ClientHandler>> rooms = new ConcurrentDictionary<string, List<ClientHandler>>();
    private static TcpListener tcpListener;

    static void Main(string[] args)
    {
        //start node
        Database.Init();
        int port = 8080;
        tcpListener = new TcpListener(IPAddress.Any, port);
        tcpListener.Start();

        Console.WriteLine($"Server started on port {port}...");

        Thread listenerThread = new Thread(ListenForClients);
        listenerThread.Start();
    }

    private static void ListenForClients()
    {
        while (true)
        {
            TcpClient newClient = tcpListener.AcceptTcpClient();
            Console.WriteLine("New client connected.");

            Thread clientThread = new Thread(HandleClientComm);
            clientThread.Start(newClient);
        }
    }

    private static void HandleClientComm(object obj)
    {
        TcpClient tcpClient = (TcpClient)obj;
        NetworkStream stream = tcpClient.GetStream();
        ClientHandler clientHandler = new ClientHandler(tcpClient, stream);

        try
        {
            string userName = AskForUserName(stream);
            string roomCode = AskForRoomCode(stream);

            Room existingRoom = Database.GetRoom(roomCode);
            if (existingRoom == null)
            {
                rooms[roomCode] = new List<ClientHandler>();
                Database.CreateRoom(roomCode);
            }
            else if (!rooms.ContainsKey(roomCode))
            {
                rooms[roomCode] = new List<ClientHandler>();
            }

            clientHandler.UserName = userName;
            rooms[roomCode].Add(clientHandler);
            Console.WriteLine($"{userName} joined room {roomCode}");

            List<Message> messages = Database.GetMessagesByRoomCode(roomCode);
            foreach (var msg in messages)
            {
                string historyMessage = $"{msg.Timestamp}: {msg.UserName}: {msg.Content}\r\n";
                SendMessage(clientHandler.Stream, historyMessage);
            }

            BroadcastMessage($"{userName} has joined the room.", roomCode, clientHandler);

            while (true)
            {
                string message = clientHandler.ReceiveMessage();
                if (string.IsNullOrEmpty(message)) break;

                if (message.StartsWith("SEND_FILE"))
                {
                    string[] parts = message.Split('|');
                    string fileName = parts[1];
                    long fileSize = long.Parse(parts[2]);

                    if (fileSize > 10 * 1024 * 1024) // 10MB
                    {
                        SendMessage(clientHandler.Stream, "File size exceeds 10MB limit. Upload rejected.\n");
                        continue;
                    }

                    string savePath = $"SendFromClient/{roomCode}/{fileName}";

                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));

                    Console.WriteLine($"Receiving file: {fileName} ({fileSize / 1024} KB)");
                    BroadcastMessage($"{userName} uploaded a file: {fileName}", roomCode, clientHandler);

                    Database.SaveFileMetadata(new FileMetadata
                    {
                        RoomCode = roomCode,
                        FileName = fileName,
                        FilePath = savePath,
                        Sender = userName,
                        Timestamp = DateTime.Now
                    });

                    using (FileStream fs = new FileStream(savePath, FileMode.Create))
                    {
                        byte[] buffer = new byte[4096];
                        long totalBytesRead = 0;
                        int bytesRead;
                        while ((bytesRead = clientHandler.Stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytesRead += bytesRead;
                            if (totalBytesRead > fileSize) break;

                            fs.Write(buffer, 0, bytesRead);
                        }
                    }



                    
                }

                //else if (message.StartsWith("GET_FILE"))
                //{
                //string[] parts = message.Split('|');
                //string fileName = parts[1];

                //FileMetadata fileMeta = Database.GetFileMetadata(roomCode, fileName);
                //if (fileMeta != null)
                //{
                //    var filePath = fileMeta.FilePath;
                //    string originalFileName = Path.GetFileName(filePath);
                //    long fileSize = new FileInfo(filePath).Length;

                //    // Gửi phản hồi tới client với thông tin tệp
                //    SendMessage(clientHandler.Stream, $"DOWNLOAD_FILE|{originalFileName}|{fileSize}");

                //    // Gửi dữ liệu tệp
                //    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                //    {
                //        byte[] buffer = new byte[4096];
                //        int bytesRead;
                //        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                //        {
                //            clientHandler.Stream.Write(buffer, 0, bytesRead);
                //        }
                //    }

                //    Console.WriteLine($"File {fileName} sent to client.");
                //}
                //else
                //{
                //    SendMessage(clientHandler.Stream, "ERROR|File not found.");
                //}






                //}

                else if (message.StartsWith("GET_FILE"))
                {
                    string[] parts = message.Split('|');
                    string fileName = parts[1];

                    FileMetadata fileMeta = Database.GetFileMetadata(roomCode, fileName);
                    if (fileMeta != null)
                    {
                        string filePath = fileMeta.FilePath;
                        string originalFileName = Path.GetFileName(filePath);
                        long fileSize = new FileInfo(filePath).Length;

                        // Gửi phản hồi tới client với thông tin tệp, thêm cờ "SAVE_FILE"
                        SendMessage(clientHandler.Stream, $"SAVE_FILE|{originalFileName}|{fileSize}");

                        // Gửi dữ liệu tệp
                        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                        {
                            byte[] buffer = new byte[4096];
                            long totalBytesSent = 0;

                            int bytesRead;
                            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                stream.Write(buffer, 0, bytesRead);
                                totalBytesSent += bytesRead;

                                Console.WriteLine($"Sent {totalBytesSent} of {fileSize} bytes.");
                            }

                            Console.WriteLine($"File {Path.GetFileName(filePath)} successfully sent.");
                        }


                        Console.WriteLine($"File {fileName} sent to client with SAVE_FILE flag.");
                    }
                    else
                    {
                        SendMessage(clientHandler.Stream, "ERROR|File not found.");
                    }
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
            {
                room.Remove(clientHandler);
            }

            clientHandler.TcpClient.Close();
        }
    }
    private static string ExtractArgument(string input)
    {
        input = input.Trim();
        if (input.StartsWith("\"") && input.EndsWith("\""))
        {
            return input.Substring(1, input.Length - 2).Replace("\\\"", "\"");
        }
        return input;
    }

    private static string AskForUserName(NetworkStream stream)
    {
        string prompt = "Enter your username: ";
        byte[] promptBytes = Encoding.UTF8.GetBytes(prompt);
        stream.Write(promptBytes, 0, promptBytes.Length);

        byte[] buffer = new byte[1024];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
    }

    private static string AskForRoomCode(NetworkStream stream)
    {
        string prompt = "Enter room code: ";
        byte[] promptBytes = Encoding.UTF8.GetBytes(prompt);
        stream.Write(promptBytes, 0, promptBytes.Length);

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
            if (clientHandler == sender) continue;

            try
            {
                clientHandler.Stream.Write(messageBytes, 0, messageBytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message to {clientHandler.UserName}: {ex.Message}");
            }
        }
    }
}

public class ClientHandler
{
    public TcpClient TcpClient { get; private set; }
    public NetworkStream Stream { get; private set; }
    public string UserName { get; set; }

    public ClientHandler(TcpClient tcpClient, NetworkStream stream)
    {
        TcpClient = tcpClient;
        Stream = stream;
    }

    public string ReceiveMessage()
    {
        byte[] buffer = new byte[4096];
        int bytesRead = Stream.Read(buffer, 0, buffer.Length);
        return bytesRead == 0 ? null : Encoding.UTF8.GetString(buffer, 0, bytesRead);
    }
}

public class Message
{
    public ObjectId Id { get; set; }
    public string RoomCode { get; set; }
    public string UserName { get; set; }
    public string Content { get; set; }
    public DateTime Timestamp { get; set; }
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

    public static List<Message> GetMessagesByRoomCode(string roomCode)
    {
        return messagesCollection.Find(m => m.RoomCode == roomCode).ToList();
    }

    public static Room GetRoom(string roomCode)
    {
        return roomsCollection.Find(r => r.RoomCode == roomCode).FirstOrDefault();
    }

    public static void CreateRoom(string roomCode)
    {
        var room = new Room { RoomCode = roomCode, CreatedAt = DateTime.Now };
        roomsCollection.InsertOne(room);
    }

    public static void SaveFileMetadata(FileMetadata fileMetadata)
    {
        filesCollection.InsertOne(fileMetadata);
    }

    public static FileMetadata GetFileMetadata(string roomCode, string fileName)
    {
        return filesCollection.Find(f => f.RoomCode == roomCode && f.FileName == fileName).FirstOrDefault();
    }
}

public class Room
{
    public ObjectId Id { get; set; }
    public string RoomCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Message> Messages { get; set; } = new List<Message>();
}
