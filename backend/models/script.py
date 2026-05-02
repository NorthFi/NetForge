from app import db
from datetime import datetime

class Script(db.Model):
    __tablename__ = 'scripts'
    
    id = db.Column(db.Integer, primary_key=True)
    user_id = db.Column(db.Integer, db.ForeignKey('users.id'), nullable=False)
    name = db.Column(db.String(120), nullable=False, index=True)
    description = db.Column(db.Text, nullable=True)
    content = db.Column(db.Text, nullable=False)
    device_type = db.Column(db.String(50), nullable=False)  # Mikrotik, Ubiquiti, etc.
    vendor = db.Column(db.String(50), default='Mikrotik', nullable=False)
    version = db.Column(db.Integer, default=1)  # Script version number
    is_active = db.Column(db.Boolean, default=True)
    tags = db.Column(db.String(255), nullable=True)  # Comma-separated tags
    created_at = db.Column(db.DateTime, default=datetime.utcnow)
    updated_at = db.Column(db.DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)
    
    def to_dict(self):
        """Convert to dictionary"""
        return {
            'id': self.id,
            'name': self.name,
            'description': self.description,
            'device_type': self.device_type,
            'vendor': self.vendor,
            'version': self.version,
            'is_active': self.is_active,
            'tags': self.tags.split(',') if self.tags else [],
            'created_at': self.created_at.isoformat() if self.created_at else None,
            'updated_at': self.updated_at.isoformat() if self.updated_at else None
        }
    
    def __repr__(self):
        return f'<Script {self.name}>'
