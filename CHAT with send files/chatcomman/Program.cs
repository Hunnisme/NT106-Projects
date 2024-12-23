using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Client
{
    private static TcpClient tcpClient;
    private static TcpClient tcpClientFile;
    private static NetworkStream stream;
    private static NetworkStream fileStream;

    static void Main(string[] args)
    {
        IPAddress[] addresses = Dns.GetHostAddresses("chat.hunn.io.vn");
        string serverAddress = string.Join(", ", Array.ConvertAll(addresses, ip => ip.ToString()));
        //string serverAddress = "127.0.0.1"; // Server IP

        int port = 8080;
        int portFile = 8081;

        IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(serverAddress), port);
        IPEndPoint endPointFile = new IPEndPoint(IPAddress.Parse(serverAddress), portFile);

        tcpClient = new TcpClient();
        tcpClientFile = new TcpClient();

        // Construct the stream message and connect to server
        tcpClient.Connect(endPoint);
        stream = tcpClient.GetStream();

        // Construct the stream file and connect to server
        tcpClientFile.Connect(endPointFile);
        fileStream = tcpClientFile.GetStream();

        Console.WriteLine("Connected to server.");

        // Ask for username and room code
        string userName = AskForUserName();
        SendMessage(userName);
        string roomCode = AskForRoomCode();
        SendMessage(roomCode);

        // Thread to receive messages
        Thread receiveThread = new Thread(ReceiveMessages); 
        receiveThread.Start();

        // Input loop
        while (true)
        {
            string input = Console.ReadLine();

            if (input.StartsWith("\\sendfile ")) 
            {
                // Send file
                string filePath = ExtractArgument(input.Substring(10).Trim());
                SendFile(filePath);
            }
            else if (input.StartsWith("\\getfile ")) 
            {
                // Request file from server
                string fileName = ExtractArgument(input.Substring(9).Trim());
                SendMessage($"GET_FILE|{fileName}");
            }
            else
            {
                // Send regular messages
                SendMessage(input);
            }
        }
    }

    private static void SendMessage(string message)
    {
        try
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            stream.Write(messageBytes, 0, messageBytes.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending message: {ex.Message}");
        }
    }
    private static string AskForUserName()
    {
        Console.Write("Enter your username: ");
        return Console.ReadLine().Trim();
    }

    private static string AskForRoomCode()
    {
        Console.Write("Enter room code: ");
        return Console.ReadLine().Trim();
    }

    

    private static void ReceiveMessages()
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            try
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                // Đảm bảo sử dụng UTF-8 để giải mã
                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (message.StartsWith("SAVE_FILE"))
                {
                    // Xử lý khi nhận file
                    string[] parts = message.Split('|');
                    string fileName = parts[1];
                    long fileSize = long.Parse(parts[2]);

                    Console.WriteLine($"Receiving file: {fileName}, Size: {fileSize / 1024} KB");

                    // Tải file xuống
                    DownloadFile(fileName, fileSize);
                }
                else
                {
                    Console.WriteLine(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection error: {ex.Message}");
                break;
            }
        }
    }


    private static void DownloadFile(string fileName, long fileSize)
    {
        try
        {
            string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "DownloadedFiles");
            Directory.CreateDirectory(saveDir);

            string savePath = Path.Combine(saveDir, fileName);
            using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[4096];
                long totalBytesRead = 0;

                while (totalBytesRead < fileSize)
                {
                    int bytesRead = fileStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    fs.Write(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    Console.WriteLine($"Downloading... {totalBytesRead}/{fileSize} bytes.");
                }

                fs.Flush();
                fs.Close();

                if (totalBytesRead == fileSize)
                {
                    Console.WriteLine($"File {fileName} downloaded successfully.");
                }
                else
                {
                    Console.WriteLine("File download incomplete.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
        }
    }

    private static void SendFile(string filePath)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }

            long fileSize = new FileInfo(filePath).Length;
            if (fileSize > 10 * 1024 * 1024)
            {
                Console.WriteLine("File size exceeds 10MB limit.");
                return;
            }

            string fileName = Path.GetFileName(filePath);
            SendMessage($"SEND_FILE|{fileName}|{fileSize}");

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fileStream.Write(buffer, 0, bytesRead);
                }
                fs.Flush();
                fs.Close();
            }

            Console.WriteLine($"File {fileName} sent successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending file: {ex.Message}");
        }
    }

    private static string ExtractArgument(string input)
    {
        return input.Trim('\'', '\"').Trim();
    }
}
