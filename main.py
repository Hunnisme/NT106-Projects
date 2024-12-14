from flask import Flask, request, jsonify, session, render_template
from pymongo import MongoClient
from werkzeug.security import generate_password_hash, check_password_hash
from bson import ObjectId
from datetime import datetime
import re

app = Flask(__name__)
app.secret_key = "your_secret_key"  # Bắt buộc cho session hoạt động

# MongoDB Connection
client = MongoClient('192.168.1.16', 27017)
db = client.DOAN_NT106  # Database
user_collection = db.user  # User Collection
projects_collection = db.project  # Project Collection

# Regex for email validation
EMAIL_REGEX = re.compile(r'^[^@]+@[^@]+\.[^@]+$')

@app.route("/",methods=['GET'])
def index():
    return render_template('index.html')

# ---------------------------- USER ROUTES ---------------------------- #
@app.route("/Create_User", methods=['POST'])
def create_user():
    # Get form data
    data = request.get_json()
    username = data.get('Username')
    email = data.get('Email')
    password = data.get('Password')
    name = data.get('Name')

    # Validate input
    if not username or not email or not password or not name:
        return jsonify({"error": "All fields are required!"}), 400

    if not EMAIL_REGEX.match(email):
        return jsonify({"error": "Invalid email format!"}), 400

    # Check if user already exists
    try:
        existing_user = user_collection.find_one({"$or": [{"Username": username}, {"Email": email}]})
        if existing_user:
            return jsonify({"error": "Username or Email already exists. Please try again!"}), 400

        # Hash password before storing
        hashed_password = generate_password_hash(password)

        # Insert user into database
        user_collection.insert_one({
            'Username': username,
            'Email': email,
            'Password': hashed_password,
            'Name': name,
            'role': 'user',  # Default role
            'CreateDate': datetime.utcnow()
        })
        return jsonify({"message": "User created successfully!"}), 201
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@app.route("/login", methods=['POST'])
def login():
    data = request.get_json()
    identifier = data.get('Identifier')  # Username or Email
    password = data.get('Password')

    if not identifier or not password:
        return jsonify({"error": "Identifier and password are required!"}), 400

    # Check user credentials
    user = user_collection.find_one({
        "$or": [{"Username": identifier}, {"Email": identifier}]
    })

    if user and check_password_hash(user['Password'], password):
        # Set session data
        session['user_id'] = str(user['_id'])
        session['username'] = user['Username']
        return jsonify({
            "message": "Login successful!",
            "username": user['Username'],
            "user_id": str(user['_id'])
        }), 200
    else:
        return jsonify({"error": "Invalid username/email or password!"}), 401


# --------------------------- PROJECT ROUTES --------------------------- #
@app.route("/createproject", methods=['POST'])
def create_project():
    data = request.get_json()
    project_name = data.get('ProjectName')
    description = data.get('Description')
    start_date = data.get('StartDate')
    end_date = data.get('EndDate')
    status = data.get('Status')
    created_by = data.get('CreatedBy')  # User ID của người tạo

    # Validate required fields
    if not project_name or not description or not start_date or not status or not created_by:
        return jsonify({"error": "Missing required fields!"}), 400

    # Validate status
    valid_statuses = ['Ongoing', 'Completed', 'Pending', 'Delayed', 'Canceled']
    if status not in valid_statuses:
        return jsonify({"error": f"Invalid status. Allowed values are: {', '.join(valid_statuses)}"}), 400

    if not ObjectId.is_valid(created_by):
        return jsonify({"error": "Invalid creator ID."}), 400

    # Validate creator exists
    creator = user_collection.find_one({"_id": ObjectId(created_by)})
    if not creator:
        return jsonify({"error": "Creator not found."}), 404

    # Create project
    create_date = datetime.utcnow()
    project = {
        'ProjectName': project_name,
        'Description': description,
        'StartDate': start_date,
        'EndDate': end_date,
        'Status': status,
        'CreatedBy': ObjectId(created_by),
        'CreateDate': create_date,
        'Members': [
            {'MemberID': ObjectId(created_by), 'Role': 'Owner'}  # Thêm người tạo vào Members
        ]
    }

    try:
        result = projects_collection.insert_one(project)
        return jsonify({"message": "Project created successfully!", "project_id": str(result.inserted_id)}), 201
    except Exception as e:
        return jsonify({"error": str(e)}), 500



@app.route("/project_members", methods=['POST'])
def add_project_members():
    data = request.get_json()
    admin_id = data.get('AdminID')
    project_id = data.get('ProjectID')
    identifiers = data.get('Identifiers')  # Danh sách Username hoặc Email
    member_role = data.get('Role')

    if not admin_id or not project_id or not identifiers or not member_role:
        return jsonify({"error": "Missing required fields!"}), 400

    if not ObjectId.is_valid(admin_id) or not ObjectId.is_valid(project_id):
        return jsonify({"error": "Invalid ID format."}), 400

    # Kiểm tra dự án tồn tại
    project = projects_collection.find_one({"_id": ObjectId(project_id)})
    if not project:
        return jsonify({"error": "Project not found."}), 404

    # Kiểm tra quyền của Admin hoặc Creator
    if project['CreatedBy'] == ObjectId(admin_id):
        user_role = "Creator"
    else:
        admin_in_project = next((member for member in project.get('Members', []) 
                                 if member['MemberID'] == ObjectId(admin_id) and member['Role'] == 'Admin'), None)
        user_role = "Admin" if admin_in_project else None

    if user_role not in ["Admin", "Creator"]:
        return jsonify({"error": "Only Admin or Creator can add project members."}), 403

    # Lọc danh sách user từ identifiers
    users = user_collection.find({"$or": [{"Username": {"$in": identifiers}}, {"Email": {"$in": identifiers}}]})
    users = list(users)  # Chuyển cursor thành list để xử lý
    if not users:
        return jsonify({"error": "No users found with provided identifiers."}), 404

    # Tạo danh sách members chưa tồn tại trong project
    new_members = []
    for user in users:
        member_id = user['_id']
        # Kiểm tra xem user đã là thành viên chưa
        existing_member = projects_collection.find_one({
            "_id": ObjectId(project_id),
            "Members.MemberID": member_id
        })
        if not existing_member:
            new_members.append({"MemberID": member_id, "Role": member_role})

    if not new_members:
        return jsonify({"error": "All members are already in the project."}), 400

    # Thêm các thành viên mới vào project
    try:
        projects_collection.update_one(
            {"_id": ObjectId(project_id)},
            {"$push": {"Members": {"$each": new_members}}}
        )
        return jsonify({
            "message": "Members added successfully!",
            "added_members": [user['Username'] for user in users]
        }), 201
    except Exception as e:
        return jsonify({"error": str(e)}), 500



@app.route("/project", methods=['GET'])
def view_project():
    project_id = request.args.get('ProjectID')
    user_id = request.args.get('UserID')

    if not project_id or not user_id:
        return jsonify({"error": "ProjectID and UserID are required!"}), 400

    if not ObjectId.is_valid(project_id) or not ObjectId.is_valid(user_id):
        return jsonify({"error": "Invalid ID format!"}), 400

    project = projects_collection.find_one({"_id": ObjectId(project_id)})
    if not project:
        return jsonify({"error": "Project not found."}), 404

    # Check if the user is a member or creator of the project
    is_member = any(member['MemberID'] == ObjectId(user_id) for member in project.get('Members', []))
    if not is_member and str(project.get('CreatedBy')) != user_id:
        return jsonify({"error": "Access denied. You are not a member of this project."}), 403

    # Get the creator's name
    creator = user_collection.find_one({"_id": ObjectId(project['CreatedBy'])})
    creator_name = creator.get('Name') if creator else "Unknown"

    # Fetch tasks for the project
    tasks = list(db.tasks.find({"ProjectID": ObjectId(project_id)}))
    for task in tasks:
        task['_id'] = str(task['_id'])
        task['AssignedTo'] = str(task['AssignedTo'])
        task['ProjectID'] = str(task['ProjectID'])
        task['CreateDate'] = task['CreateDate'].isoformat()
        if 'DueDate' in task:
            task['DueDate'] = task['DueDate']

    # Build the response
    response = {
        "ProjectName": project['ProjectName'],
        "Description": project['Description'],
        "StartDate": project['StartDate'],
        "EndDate": project.get('EndDate'),
        "Status": project['Status'],
        "CreatedBy": creator_name,
        "CreateDate": project['CreateDate'].isoformat(),
        "Tasks": tasks  # Include tasks in the project
    }
    return jsonify(response), 200


@app.route("/user_projects", methods=['GET'])
def user_projects():
    user_id = request.args.get('UserID')
    
    if not user_id:
        return jsonify({"error": "UserID is required!"}), 400

    if not ObjectId.is_valid(user_id):
        return jsonify({"error": "Invalid UserID format!"}), 400

    try:
        # Lấy tất cả các dự án mà người dùng tạo ra hoặc tham gia
        projects = list(projects_collection.find({
            "$or": [
                {"CreatedBy": ObjectId(user_id)},
                {"Members.MemberID": ObjectId(user_id)}
            ]
        }))

        if not projects:
            return jsonify({"message": "No projects found for this user."}), 404

        # Lấy danh sách UserID của những người tạo dự án
        creator_ids = {project['CreatedBy'] for project in projects}
        creators = user_collection.find({"_id": {"$in": list(creator_ids)}})
        creator_map = {str(creator['_id']): creator['Name'] for creator in creators}

        # Chuẩn bị dữ liệu trả về
        result = []
        for project in projects:
            # Tìm role của người dùng trong project
            user_role = None
            if project['CreatedBy'] == ObjectId(user_id):
                user_role = "Creator"
            else:  # Nếu không phải Creator, tìm role trong Members
                for member in project.get('Members', []):
                    if member['MemberID'] == ObjectId(user_id):
                        user_role = member.get('Role', "Member")
                        break

            # Xác định quyền nếu user là Admin
            if user_role == "Creator":
                user_role = "Admin/Creator"  # Đánh dấu rõ quyền tương tự

            creator_name = creator_map.get(str(project['CreatedBy']), "Unknown")  # Default là "Unknown"
            result.append({
                "ProjectID": str(project['_id']),
                "ProjectName": project['ProjectName'],
                "Description": project['Description'],
                "Status": project['Status'],
                "StartDate": project['StartDate'],
                "EndDate": project.get('EndDate', None),
                "CreatedBy": creator_name,  # Thay CreatedBy bằng tên
                "CreateDate": project['CreateDate'].isoformat(),
                "UserRole": user_role  # Thêm role của user trong project
            })

        return jsonify({"projects": result}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500
@app.route("/update_member_role", methods=['PUT'])
def update_member_role():
    data = request.get_json()
    admin_id = data.get('AdminID')  # ID của người thực hiện thay đổi
    project_id = data.get('ProjectID')  # ID của dự án
    identifier = data.get('Identifier')  # Username hoặc Email của thành viên cần thay đổi
    new_role = data.get('Role')  # Vai trò mới

    valid_roles = ['Owner', 'Admin', 'Member', 'Viewer']  # Các vai trò hợp lệ
    
    # Validate input
    if not admin_id or not project_id or not identifier or not new_role:
        return jsonify({"error": "AdminID, ProjectID, Identifier, and Role are required!"}), 400

    if not ObjectId.is_valid(admin_id) or not ObjectId.is_valid(project_id):
        return jsonify({"error": "Invalid ID format!"}), 400

    if new_role not in valid_roles:
        return jsonify({"error": f"Invalid role. Allowed roles are: {', '.join(valid_roles)}"}), 400
    
    try:
        # Tìm thành viên dựa trên Username hoặc Email
        member = user_collection.find_one({"$or": [{"Username": identifier}, {"Email": identifier}]})
        if not member:
            return jsonify({"error": "Member not found with the provided Username or Email."}), 404

        member_id = member['_id']  # Lấy MemberID từ kết quả truy vấn

        # Lấy thông tin dự án
        project = projects_collection.find_one({"_id": ObjectId(project_id)})
        if not project:
            return jsonify({"error": "Project not found."}), 404

        # Lấy danh sách members
        members = project.get('Members', [])
        admin_role = next((member['Role'] for member in members if member['MemberID'] == ObjectId(admin_id)), None)
        target_member_role = next((member['Role'] for member in members if member['MemberID'] == ObjectId(member_id)), None)
         # Kiểm tra quyền của Admin hoặc Creator
        if project['CreatedBy'] == ObjectId(admin_id):
            admin_role = "Creator"
        else:
            admin_in_project = next((member for member in project.get('Members', []) 
                                    if member['MemberID'] == ObjectId(admin_id) and member['Role'] == 'Admin'), None)
            admin_role = "Admin" if admin_in_project else None

        # Kiểm tra xem AdminID có quyền hay không
        if admin_role not in ['Admin', 'Owner','Creator']:
            return jsonify({"error": "Only Admin or Creator can update member roles."}), 403

        # Creator (Owner) có quyền tối cao
        if admin_role == 'Owner' or admin_role=='Creator':
            # Creator có thể chỉnh sửa tất cả các role, bao gồm Admin
            pass
        elif admin_role == 'Admin':
            # Admin không thể chỉnh sửa hoặc xóa role của Creator
            if target_member_role == 'Owner':
                return jsonify({"error": "Admin cannot change or remove the role of the Creator (Owner)."}), 403
            # Admin chỉ được phép chỉnh sửa các thành viên cấp dưới (Member, Viewer)
            if target_member_role == 'Admin':
                return jsonify({"error": "Admin cannot change the role of another Admin."}), 403
        if admin_role == 'Admin' and new_role == 'Owner':
            return jsonify({"error": "Admin cannot change another user's role to Owner."}), 403
        # if admin_id==member_id:
        #     return jsonify({"error": "Cannot change your own role."})
        # Cập nhật vai trò của thành viên
        result = projects_collection.update_one(
            {"_id": ObjectId(project_id), "Members.MemberID": ObjectId(member_id)},
            {"$set": {"Members.$.Role": new_role}}
        )
        if result.matched_count == 0:
            return jsonify({"error": "Failed to update member role."}), 500

        return jsonify({"message": "Member role updated successfully!"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/deleteproject", methods=['DELETE'])
def delete_project():
    data = request.get_json()
    admin_id = data.get('AdminID')  # ID of the user attempting to delete the project
    project_id = data.get('ProjectID')  # ID of the project to delete

    if not admin_id or not project_id:
        return jsonify({"error": "AdminID and ProjectID are required!"}), 400

    if not ObjectId.is_valid(admin_id) or not ObjectId.is_valid(project_id):
        return jsonify({"error": "Invalid ID format!"}), 400

    try:
        # Fetch the project
        project = projects_collection.find_one({"_id": ObjectId(project_id)})
        if not project:
            return jsonify({"error": "Project not found."}), 404

        # Check if the admin_id is the creator of the project
        if project['CreatedBy'] == ObjectId(admin_id):
            user_role = "Creator"
        else:
            admin_in_project = next((member for member in project.get('Members', [])
                                     if member['MemberID'] == ObjectId(admin_id) and member['Role'] == 'Owner'), None)
            user_role = "Owner" if admin_in_project else None

        if user_role not in ["Creator", "Owner"]:
            return jsonify({"error": "Only the Creator or Owner can delete the project."}), 403

        # Delete the project
        result = projects_collection.delete_one({"_id": ObjectId(project_id)})
        if result.deleted_count == 0:
            return jsonify({"error": "Failed to delete project."}), 500

        # Optionally, delete associated tasks
        db.tasks.delete_many({"ProjectID": ObjectId(project_id)})

        return jsonify({"message": "Project deleted successfully!"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@app.route("/create_task", methods=['POST'])
def create_task():
    data = request.get_json()
    admin_id = data.get('AdminID')  # Người tạo công việc
    project_id = data.get('ProjectID')
    assigned_to = data.get('AssignedTo')
    task_name = data.get('TaskName')
    description = data.get('Description')
    due_date = data.get('DueDate')
    status = data.get('Status', 'Pending')

    if not admin_id or not project_id or not task_name or not due_date:
        return jsonify({"error": "Missing required fields!"}), 400

    if not ObjectId.is_valid(admin_id) or not ObjectId.is_valid(project_id) or not ObjectId.is_valid(assigned_to):
        return jsonify({"error": "Invalid ID format!"}), 400

    # Check if project exists
    project = projects_collection.find_one({"_id": ObjectId(project_id)})
    if not project:
        return jsonify({"error": "Project not found."}), 404

    # Check if admin has the right role (Owner or Leader)
    is_allowed = any(member['MemberID'] == ObjectId(admin_id) and member['Role'] in ['Owner', 'Leader']
                     for member in project['Members'])
    if not is_allowed:
        return jsonify({"error": "You do not have permission to create tasks in this project."}), 403

    # Create task
    task = {
        'ProjectID': ObjectId(project_id),
        'AssignedTo': ObjectId(assigned_to),
        'TaskName': task_name,
        'Description': description,
        'DueDate': due_date,
        'Status': status,
        'CreateDate': datetime.utcnow()
    }
    try:
        db.tasks.insert_one(task)
        return jsonify({"message": "Task created successfully!"}), 201
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@app.route("/update_task", methods=['PUT'])
def update_task():
    data = request.get_json()
    task_id = data.get('TaskID')
    updates = {
        "TaskName": data.get('TaskName'),
        "Description": data.get('Description'),
        "DueDate": data.get('DueDate'),
        "Status": data.get('Status'),
        "AssignedTo": data.get('AssignedTo')
    }

    if not task_id:
        return jsonify({"error": "TaskID is required!"}), 400

    if not ObjectId.is_valid(task_id):
        return jsonify({"error": "Invalid TaskID format!"}), 400

    # Remove None values
    updates = {key: value for key, value in updates.items() if value is not None}

    try:
        result = db.tasks.update_one({"_id": ObjectId(task_id)}, {"$set": updates})
        if result.matched_count == 0:
            return jsonify({"error": "Task not found."}), 404
        return jsonify({"message": "Task updated successfully!"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

# --------------------------- TASK ROUTES --------------------------- #
@app.route("/tasks", methods=['GET'])
def list_tasks():
    project_id = request.args.get('ProjectID')

    if not project_id or not ObjectId.is_valid(project_id):
        return jsonify({"error": "Valid ProjectID is required!"}), 400

    try:
        tasks = list(db.tasks.find({"ProjectID": ObjectId(project_id)}))
        for task in tasks:
            task['_id'] = str(task['_id'])
            task['ProjectID'] = str(task['ProjectID'])
            task['AssignedTo'] = str(task['AssignedTo'])

        return jsonify({"tasks": tasks}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/update_task_progress", methods=['PUT'])
def update_task_progress():
    data = request.get_json()
    task_id = data.get('TaskID')
    progress = data.get('Progress')  # Progress as a percentage

    if not task_id or progress is None:
        return jsonify({"error": "TaskID and Progress are required!"}), 400

    if not ObjectId.is_valid(task_id):
        return jsonify({"error": "Invalid TaskID format!"}), 400

    try:
        result = db.tasks.update_one({"_id": ObjectId(task_id)}, {"$set": {"Progress": progress}})
        if result.matched_count == 0:
            return jsonify({"error": "Task not found."}), 404
        return jsonify({"message": "Progress updated successfully!"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/project_report", methods=['GET'])
def project_report():
    project_id = request.args.get('ProjectID')

    if not project_id or not ObjectId.is_valid(project_id):
        return jsonify({"error": "Valid ProjectID is required!"}), 400

    try:
        tasks = list(db.tasks.find({"ProjectID": ObjectId(project_id)}))
        total_tasks = len(tasks)
        completed_tasks = sum(1 for task in tasks if task['Status'] == 'Completed')

        report = {
            "TotalTasks": total_tasks,
            "CompletedTasks": completed_tasks,
            "Progress": round((completed_tasks / total_tasks) * 100, 2) if total_tasks > 0 else 0
        }
        return jsonify({"report": report}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500
    
    




@app.route("/progress_report", methods=['GET'])
def progress_report():
    user_id = request.args.get('UserID')

    if not user_id:
        return jsonify({"error": "UserID is required!"}), 400

    if not ObjectId.is_valid(user_id):
        return jsonify({"error": "Invalid UserID format!"}), 400

    try:
        # Lấy danh sách các dự án mà người dùng tham gia hoặc quản lý
        projects = list(projects_collection.find({
            "$or": [
                {"Members.MemberID": ObjectId(user_id)},
                {"CreatedBy": ObjectId(user_id)}
            ]
        }))

        if not projects:
            return jsonify({"error": "No projects found for this user."}), 404

        # Duyệt qua từng dự án và tổng hợp dữ liệu
        report = []
        for project in projects:
            project_id = project['_id']
            project_name = project['ProjectName']

            # Lấy tất cả nhiệm vụ thuộc dự án
            tasks = list(db.tasks.find({"ProjectID": project_id}))
            total_tasks = len(tasks)
            completed_tasks = sum(1 for task in tasks if task['Status'] == 'Completed')
            ongoing_tasks = sum(1 for task in tasks if task['Status'] == 'Ongoing')
            overdue_tasks = sum(
                1 for task in tasks if task['DueDate'] and task['Status'] != 'Completed' and datetime.strptime(task['DueDate'], '%Y-%m-%d') < datetime.utcnow()
            )

            # Tính phần trăm hoàn thành
            progress = round((completed_tasks / total_tasks) * 100, 2) if total_tasks > 0 else 0

            # Thêm dữ liệu vào báo cáo
            report.append({
                "ProjectName": project_name,
                "TotalTasks": total_tasks,
                "CompletedTasks": completed_tasks,
                "OngoingTasks": ongoing_tasks,
                "OverdueTasks": overdue_tasks,
                "Progress": progress
            })

        return jsonify({"report": report}), 200

    except Exception as e:
        return jsonify({"error": str(e)}), 500


@app.route("/search_projects", methods=['GET'])
def search_projects():
    user_id = request.args.get('UserID')
    search_keyword = request.args.get('SearchKeyword', '').strip()
    status = request.args.get('Status', '').strip()
    start_date = request.args.get('StartDate', '').strip()
    end_date = request.args.get('EndDate', '').strip()
    page = int(request.args.get('Page', 1))
    page_size = int(request.args.get('PageSize', 10))

    if not user_id:
        return jsonify({"error": "UserID is required!"}), 400

    if not ObjectId.is_valid(user_id):
        return jsonify({"error": "Invalid UserID format!"}), 400

    # Validate page and page size
    if page <= 0 or page_size <= 0:
        return jsonify({"error": "Page and PageSize must be positive integers!"}), 400

    try:
        # Build query
        query = {
            "$or": [
                {"Members.MemberID": ObjectId(user_id)},
                {"CreatedBy": ObjectId(user_id)}
            ]
        }

        if search_keyword:
            query["ProjectName"] = {"$regex": search_keyword, "$options": "i"}  # Case-insensitive search

        if status:
            query["Status"] = status

        if start_date or end_date:
            date_query = {}
            if start_date:
                date_query["$gte"] = datetime.strptime(start_date, "%Y-%m-%d")
            if end_date:
                date_query["$lte"] = datetime.strptime(end_date, "%Y-%m-%d")
            query["CreateDate"] = date_query

        # Count total projects
        total_projects = projects_collection.count_documents(query)

        # Apply pagination
        projects = list(projects_collection.find(query)
                        .skip((page - 1) * page_size)
                        .limit(page_size))

        # Format output
        for project in projects:
            project['_id'] = str(project['_id'])
            project['CreatedBy'] = str(project['CreatedBy'])
            project['CreateDate'] = project['CreateDate'].isoformat()

        return jsonify({
            "total_projects": total_projects,
            "page": page,
            "page_size": page_size,
            "projects": projects
        }), 200

    except Exception as e:
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    app.run(host='0.0.0.0', debug=True)  