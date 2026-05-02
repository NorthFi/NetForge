from app import db
from datetime import datetime

class Device(db.Model):
    __tablename__ = 'devices'
    
    id = db.Column(db.Integer, primary_key=True)
    user_id = db.Column(db.Integer, db.ForeignKey('users.id'), nullable=False)
    name = db.Column(db.String(120), nullable=False)
    ip_address = db.Column(db.String(45), nullable=False)  # IPv4 or IPv6
    serial = db.Column(db.String(120), nullable=True, index=True)
    device_type = db.Column(db.String(50), nullable=False)  # Mikrotik, Ubiquiti, Cisco, etc.
    vendor = db.Column(db.String(50), default='Mikrotik', nullable=False)
    version = db.Column(db.String(50), nullable=True)
    board = db.Column(db.String(120), nullable=True)
    password_hash = db.Column(db.String(255), nullable=True)
    port = db.Column(db.Integer, default=22)
    protocol = db.Column(db.String(20), default='ssh')  # ssh, telnet, api, etc.
    notes = db.Column(db.Text, nullable=True)
    is_active = db.Column(db.Boolean, default=True)
    last_backup = db.Column(db.DateTime, nullable=True)
    last_config_update = db.Column(db.DateTime, nullable=True)
    created_at = db.Column(db.DateTime, default=datetime.utcnow)
    updated_at = db.Column(db.DateTime, default=datetime.utcnow, onupdate=datetime.utcnow)
    
    # Relationships
    backups = db.relationship('Backup', backref='device', lazy=True, cascade='all, delete-orphan')
    configs = db.relationship('Config', backref='device', lazy=True, cascade='all, delete-orphan')
    
    def to_dict(self):
        """Convert to dictionary"""
        return {
            'id': self.id,
            'name': self.name,
            'ip_address': self.ip_address,
            'serial': self.serial,
            'device_type': self.device_type,
            'vendor': self.vendor,
            'version': self.version,
            'board': self.board,
            'port': self.port,
            'protocol': self.protocol,
            'is_active': self.is_active,
            'last_backup': self.last_backup.isoformat() if self.last_backup else None,
            'last_config_update': self.last_config_update.isoformat() if self.last_config_update else None,
            'created_at': self.created_at.isoformat() if self.created_at else None,
            'updated_at': self.updated_at.isoformat() if self.updated_at else None
        }
    
    def __repr__(self):
        return f'<Device {self.name} ({self.ip_address})>'
