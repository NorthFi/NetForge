# NetForge

🔧 A comprehensive web application for managing Mikrotik and multi-vendor network equipment with backup management, configuration storage, and script deployment.

## Features

✅ **Device Management**
- Add, monitor, and manage Mikrotik and multi-vendor equipment
- Track device metadata (serial, version, board, type)
- Real-time device status and versioning

✅ **Backup Management** (Priority Feature)
- Upload and store device backups (.rsc, .bin files)
- Automatic backup versioning with timestamps
- Backup history and recovery options
- Checksum verification for data integrity
- Multi-vendor backup support

✅ **Script Management**
- Create and manage device scripts
- Deploy scripts to multiple devices
- Script execution tracking and history

✅ **Configuration Storage**
- Store and manage device configurations
- Config versioning and history
- Easy config deployment

✅ **Authentication & Security**
- User authentication with JWT tokens
- Password hashing and security
- Role-based access control (ready for implementation)

## Tech Stack

**Backend:**
- Python 3.9+
- Flask & Flask-RESTful
- SQLAlchemy ORM
- PostgreSQL / SQLite
- JWT Authentication

**Frontend:** (Coming Soon)
- React / Modern JavaScript
- Responsive Design
- Material-UI or similar

**Infrastructure:**
- Docker & Docker Compose
- RESTful API
- File Storage (Local + Database)

## Quick Start

### Prerequisites
- Python 3.9+
- Docker & Docker Compose (optional)
- PostgreSQL (optional, SQLite for development)

### Installation

#### Option 1: Using Docker
```bash
git clone https://github.com/NorthFi/NetForge.git
cd NetForge
docker-compose up
```

Access the API at `http://localhost:5000`

#### Option 2: Manual Setup
```bash
git clone https://github.com/NorthFi/NetForge.git
cd NetForge/backend

# Create virtual environment
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt

# Set up database
flask db upgrade

# Run the application
python app.py
```

The API will be available at `http://localhost:5000`

## API Documentation

### Authentication
```bash
# Register
POST /api/auth/register
{
  "username": "user@example.com",
  "password": "password123"
}

# Login
POST /api/auth/login
{
  "username": "user@example.com",
  "password": "password123"
}
```

### Devices
```bash
# Get all devices
GET /api/devices

# Add device
POST /api/devices
{
  "name": "Router-01",
  "ip_address": "192.168.1.1",
  "serial": "565.Sebela.BR01",
  "device_type": "Mikrotik",
  "version": "7.22",
  "board": "RB4011iGS",
  "password": "encrypted_password"
}

# Get device
GET /api/devices/{device_id}

# Update device
PUT /api/devices/{device_id}

# Delete device
DELETE /api/devices/{device_id}
```

### Backups
```bash
# Get all backups for a device
GET /api/devices/{device_id}/backups

# Upload backup
POST /api/devices/{device_id}/backups
Form Data:
  file: <binary file>
  notes: "Optional backup notes"

# Download backup
GET /api/backups/{backup_id}/download

# Get backup details
GET /api/backups/{backup_id}

# Delete backup
DELETE /api/backups/{backup_id}
```

### Scripts
```bash
# Get all scripts
GET /api/scripts

# Create script
POST /api/scripts
{
  "name": "Script Name",
  "content": "script content here",
  "device_type": "Mikrotik"
}

# Deploy script to device
POST /api/scripts/{script_id}/deploy/{device_id}

# Get script
GET /api/scripts/{script_id}
```

### Configurations
```bash
# Get all configs
GET /api/configs

# Create config
POST /api/configs
{
  "device_id": "device_id",
  "name": "Config Name",
  "content": "configuration data",
  "config_type": "rsc"
}

# Get config
GET /api/configs/{config_id}

# Download config
GET /api/configs/{config_id}/download
```

## Project Structure

```
NetForge/
├── backend/
│   ├── app.py                 # Main Flask application
│   ├── config.py              # Configuration settings
│   ├── requirements.txt        # Python dependencies
│   ├── models/
│   │   ├── __init__.py
│   │   ├── user.py            # User model
│   │   ├── device.py          # Device model
│   │   ├── backup.py          # Backup model
│   │   ├── script.py          # Script model
│   │   └── config.py          # Configuration model
│   ├── routes/
│   │   ├── __init__.py
│   │   ├── auth.py            # Authentication routes
│   │   ├── devices.py         # Device routes
│   │   ├── backups.py         # Backup routes
│   │   ├── scripts.py         # Script routes
│   │   └── configs.py         # Configuration routes
│   ├── utils/
│   │   ├── __init__.py
│   │   ├── decorators.py      # Auth decorators
│   │   ├── validators.py      # Input validators
│   │   └── file_handler.py    # File operations
│   └── migrations/            # Database migrations
├── frontend/                  # React application (coming soon)
├── backups/                   # Local backup storage
├── configs/                   # Local config storage
├── .gitignore
├── docker-compose.yml
├── Dockerfile
└── SETUP.md
```

## Environment Variables

Create a `.env` file in the backend directory:

```env
FLASK_ENV=development
FLASK_APP=app.py
SECRET_KEY=your-secret-key-here
DATABASE_URL=postgresql://user:password@localhost:5432/netforge
JWT_SECRET_KEY=your-jwt-secret-key
BACKUP_STORAGE_PATH=./backups
CONFIG_STORAGE_PATH=./configs
```

## Development Roadmap

- [x] Project initialization
- [x] Database models setup
- [ ] Backend API implementation (in progress)
- [ ] Frontend React application
- [ ] Mikrotik SSH/API integration
- [ ] Automated backup scheduling
- [ ] File encryption for sensitive data
- [ ] WebSocket real-time updates
- [ ] Multi-vendor integration (Ubiquiti, Cisco, etc.)
- [ ] Advanced search and filtering
- [ ] Backup comparison tools
- [ ] Configuration templates
- [ ] Audit logging
- [ ] Docker image optimization

## Contributing

Contributions are welcome! Please follow these steps:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit changes (`git commit -m 'Add amazing feature'`)
4. Push to branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

MIT License - See LICENSE file for details

## Support

For issues, feature requests, or questions, please open an issue on GitHub.

## Changelog

### v0.1.0 (Initial Release)
- Project initialization
- Database schema design
- API endpoint structure
- Authentication system
- Backup management framework
