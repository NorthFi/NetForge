# NetForge Setup Guide

## Development Environment Setup

### Step 1: Prerequisites

- Python 3.9 or higher
- PostgreSQL 12+ (or use SQLite for development)
- Docker & Docker Compose (optional)
- Git
- pip (Python package manager)

### Step 2: Clone Repository

```bash
git clone https://github.com/NorthFi/NetForge.git
cd NetForge
```

### Step 3: Backend Setup (Python)

#### Using Docker (Recommended)

```bash
docker-compose up
```

This will:
- Start PostgreSQL database
- Install Python dependencies
- Create database tables
- Run the Flask application

Access the API at `http://localhost:5000`

#### Manual Setup

```bash
cd backend

# Create virtual environment
python -m venv venv

# Activate virtual environment
# On macOS/Linux:
source venv/bin/activate
# On Windows:
venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt

# Create .env file
cp .env.example .env

# Initialize database
flask db upgrade

# Run the application
python app.py
```

### Step 4: Database Setup

The application supports both PostgreSQL and SQLite:

**Development (SQLite - Recommended for quick start):**
```env
DATABASE_URL=sqlite:///netforge.db
```

**Production (PostgreSQL):**
```env
DATABASE_URL=postgresql://user:password@localhost:5432/netforge
```

### Step 5: Create Admin User

```bash
python
>>> from app import app, db
>>> from models.user import User
>>> with app.app_context():
...     admin = User(username='admin', email='admin@netforge.local')
...     admin.set_password('admin123')
...     db.session.add(admin)
...     db.session.commit()
>>> exit()
```

### Step 6: Test the API

```bash
# Test registration
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"test123"}'

# Test login
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"test123"}'
```

## Configuration

### Environment Variables

Create a `.env` file in the `backend/` directory:

```env
# Flask Configuration
FLASK_ENV=development
FLASK_APP=app.py
FLASK_DEBUG=True

# Secret Keys
SECRET_KEY=your-super-secret-key-change-in-production
JWT_SECRET_KEY=your-jwt-secret-key-change-in-production
JWT_ALGORITHM=HS256

# Database
DATABASE_URL=sqlite:///netforge.db
# For PostgreSQL:
# DATABASE_URL=postgresql://user:password@localhost:5432/netforge

# File Storage
BACKUP_STORAGE_PATH=./backups
CONFIG_STORAGE_PATH=./configs
MAX_BACKUP_SIZE=500MB  # Maximum backup file size

# API
API_HOST=0.0.0.0
API_PORT=5000

# Logging
LOG_LEVEL=INFO
LOG_FILE=./logs/netforge.log

# CORS (For frontend integration)
CORS_ORIGINS=http://localhost:3000,http://localhost:5173
```

## Docker Setup

### Using Docker Compose

```bash
# Build and run
docker-compose up -d

# View logs
docker-compose logs -f

# Stop services
docker-compose down
```

### Docker Compose Services

- **web**: Flask API (port 5000)
- **db**: PostgreSQL (port 5432)
- **adminer**: Database management UI (port 8080) - optional

## Frontend Setup (Coming Soon)

React application setup instructions will be added when frontend development begins.

## Troubleshooting

### Database Connection Error

```bash
# Check if PostgreSQL is running
sudo systemctl status postgresql

# Or use SQLite for development
export DATABASE_URL=sqlite:///netforge.db
```

### Port Already in Use

```bash
# Change port in .env or command line
FLASK_RUN_PORT=5001 python app.py
```

### ModuleNotFoundError

```bash
# Ensure virtual environment is activated and dependencies installed
source venv/bin/activate
pip install -r requirements.txt
```

### Database Migration Issues

```bash
# Reset database
rm netforge.db  # For SQLite
# Or drop PostgreSQL database

# Re-initialize
flask db upgrade
```

## Testing

```bash
# Run tests
pytest tests/

# With coverage
pytest --cov=app tests/
```

## Performance Tuning

### Database Optimization

- Use PostgreSQL for production
- Enable connection pooling
- Add indexes on frequently queried columns

### API Optimization

- Implement caching for frequently accessed data
- Use pagination for large result sets
- Add rate limiting for API endpoints

## Security

### Before Production Deployment

1. Change all SECRET_KEY values
2. Use environment variables for sensitive data
3. Enable HTTPS/SSL
4. Set up firewall rules
5. Enable database backups
6. Implement rate limiting
7. Add CORS restrictions
8. Enable audit logging

## Next Steps

1. Explore the API endpoints
2. Create sample devices and backups
3. Review the database schema
4. Plan frontend development
5. Set up CI/CD pipeline

## Support

For issues or questions, open an issue on GitHub.
