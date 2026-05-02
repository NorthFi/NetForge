from app import db
from datetime import datetime
import hashlib

class Backup(db.Model):
    __tablename__ = 'backups'
    
    id = db.Column(db.Integer, primary_key=True)
    device_id = db.Column(db.Integer, db.ForeignKey('devices.id'), nullable=False)
    user_id = db.Column(db.Integer, db.ForeignKey('users.id'), nullable=False)
    filename = db.Column(db.String(255), nullable=False)
    file_path = db.Column(db.String(255), nullable=False, unique=True)
    file_size = db.Column(db.Integer, nullable=False)  # Size in bytes
    file_type = db.Column(db.String(20), nullable=False)  # .rsc, .bin, .backup, etc.
    checksum = db.Column(db.String(64), nullable=True)  # SHA256 hash
    version = db.Column(db.String(50), nullable=True)  # Device version when backed up
    notes = db.Column(db.Text, nullable=True)
    is_encrypted = db.Column(db.Boolean, default=False)
    backup_method = db.Column(db.String(50), default='manual')  # manual, auto, scheduled
    created_at = db.Column(db.DateTime, default=datetime.utcnow, index=True)
    updated_at = db.Column(db.DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)
    
    def to_dict(self):
        """Convert to dictionary"""
        return {
            'id': self.id,
            'device_id': self.device_id,
            'filename': self.filename,
            'file_size': self.file_size,
            'file_type': self.file_type,
            'checksum': self.checksum,
            'version': self.version,
            'notes': self.notes,
            'is_encrypted': self.is_encrypted,
            'backup_method': self.backup_method,
            'created_at': self.created_at.isoformat() if self.created_at else None,
            'updated_at': self.updated_at.isoformat() if self.updated_at else None
        }
    
    def __repr__(self):
        return f'<Backup {self.filename} ({self.created_at})>'
