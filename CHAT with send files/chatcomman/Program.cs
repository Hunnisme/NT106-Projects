using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Client
{
    private static TcpClient tcpClient;
    private static NetworkStream stream;

    static void Main(string[] args)
    {
        IPAddress[] addresses = Dns.GetHostAddresses("chat.hunn.io.vn");
        string serverAddress = string.Join(", ", Array.ConvertAll(addresses, ip => ip.ToString()));
        //string serverAddress = "127.0.0.1";
        int port = 8080;

        tcpClient = new TcpClient(serverAddress, port);
        stream = tcpClient.GetStream();

        Console.WriteLine("Connected to server.");

        // Yêu cầu người dùng nhập tên
        string userName = AskForUserName();
        SendMessage(userName);

        // Yêu cầu người dùng nhập mã phòng
        string roomCode = AskForRoomCode();
        SendMessage(roomCode);

        // Tạo luồng để nhận tin nhắn từ server
        Thread receiveThread = new Thread(ReceiveMessages);
        receiveThread.Start();

        // Gửi tin nhắn hoặc xử lý lệnh
        while (true)
        {
            string input = Console.ReadLine();

            if (input.StartsWith("\\sendfile "))
            {
                // Gửi file
                string filePath = ExtractArgument(input.Substring(10).Trim());
                SendFile(filePath);
            }
            else if (input.StartsWith("\\getfile "))
            {
                // Tải file
                string fileName = ExtractArgument(input.Substring(9).Trim());
                SendMessage($"GET_FILE|{fileName}");
            }
            else
            {
                // Gửi tin nhắn thông thường
                SendMessage(input);
            }
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

    private static void ReceiveMessages()
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            try
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (message.StartsWith("SAVE_FILE"))
                {
                    // Xử lý khi nhận file từ server
                    string[] parts = message.Split('|');
                    string fileName = parts[1];
                    long fileSize = long.Parse(parts[2]);

                    Console.WriteLine($"Receiving file: {fileName}, Size: {fileSize / 1024} KB");

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
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    fs.Write(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    Console.WriteLine($"Downloading... {totalBytesRead}/{fileSize} bytes.");
                }

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
                    stream.Write(buffer, 0, bytesRead);
                }
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
