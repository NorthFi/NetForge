from flask import Blueprint, request, jsonify, send_file
from flask_jwt_extended import jwt_required, get_jwt_identity
from werkzeug.utils import secure_filename
from app import db, app
from models.backup import Backup
from models.device import Device
import os
import hashlib
from datetime import datetime

backups_bp = Blueprint('backups', __name__)

ALLOWED_EXTENSIONS = {'rsc', 'bin', 'backup', 'conf', 'cfg', 'txt', 'json'}

def allowed_file(filename):
    return '.' in filename and filename.rsplit('.', 1)[1].lower() in ALLOWED_EXTENSIONS

def calculate_file_hash(filepath):
    """Calculate SHA256 hash of a file"""
    sha256_hash = hashlib.sha256()
    with open(filepath, "rb") as f:
        for byte_block in iter(lambda: f.read(4096), b""):
            sha256_hash.update(byte_block)
    return sha256_hash.hexdigest()

@backups_bp.route('', methods=['GET'])
@jwt_required()
def get_backups():
    """Get all backups for current user"""
    user_id = get_jwt_identity()
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 20, type=int)
    device_id = request.args.get('device_id', type=int)
    
    query = Backup.query.filter_by(user_id=user_id)
    
    if device_id:
        query = query.filter_by(device_id=device_id)
    
    backups = query.order_by(Backup.created_at.desc()).paginate(page=page, per_page=per_page)
    
    return jsonify({
        'data': [backup.to_dict() for backup in backups.items],
        'pagination': {
            'page': page,
            'per_page': per_page,
            'total': backups.total,
            'pages': backups.pages
        }
    }), 200

@backups_bp.route('/<int:backup_id>', methods=['GET'])
@jwt_required()
def get_backup(backup_id):
    """Get backup details"""
    user_id = get_jwt_identity()
    backup = Backup.query.filter_by(id=backup_id, user_id=user_id).first()
    
    if not backup:
        return jsonify({'error': 'Backup not found'}), 404
    
    return jsonify(backup.to_dict()), 200

@backups_bp.route('/device/<int:device_id>', methods=['GET'])
@jwt_required()
def get_device_backups(device_id):
    """Get all backups for a specific device"""
    user_id = get_jwt_identity()
    device = Device.query.filter_by(id=device_id, user_id=user_id).first()
    
    if not device:
        return jsonify({'error': 'Device not found'}), 404
    
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 20, type=int)
    
    backups = Backup.query.filter_by(device_id=device_id).order_by(
        Backup.created_at.desc()
    ).paginate(page=page, per_page=per_page)
    
    return jsonify({
        'data': [backup.to_dict() for backup in backups.items],
        'pagination': {
            'page': page,
            'per_page': per_page,
            'total': backups.total,
            'pages': backups.pages
        }
    }), 200

@backups_bp.route('/device/<int:device_id>/upload', methods=['POST'])
@jwt_required()
def upload_backup(device_id):
    """Upload a backup file for a device"""
    user_id = get_jwt_identity()
    device = Device.query.filter_by(id=device_id, user_id=user_id).first()
    
    if not device:
        return jsonify({'error': 'Device not found'}), 404
    
    if 'file' not in request.files:
        return jsonify({'error': 'No file provided'}), 400
    
    file = request.files['file']
    
    if file.filename == '':
        return jsonify({'error': 'No file selected'}), 400
    
    if not allowed_file(file.filename):
        return jsonify({'error': 'File type not allowed'}), 400
    
    # Check file size (default 500MB)
    max_size = app.config.get('MAX_BACKUP_SIZE', 500 * 1024 * 1024)
    if len(file.read()) > max_size:
        file.seek(0)
        return jsonify({'error': f'File size exceeds {max_size} bytes'}), 413
    file.seek(0)
    
    try:
        filename = secure_filename(file.filename)
        
        # Create backup directory if it doesn't exist
        backup_dir = app.config['BACKUP_STORAGE_PATH']
        os.makedirs(backup_dir, exist_ok=True)
        
        # Create unique filename with timestamp
        timestamp = datetime.utcnow().strftime('%Y%m%d_%H%M%S')
        unique_filename = f"{device_id}_{timestamp}_{filename}"
        file_path = os.path.join(backup_dir, unique_filename)
        
        # Save file
        file.save(file_path)
        
        # Calculate file hash
        file_hash = calculate_file_hash(file_path)
        file_size = os.path.getsize(file_path)
        file_type = filename.rsplit('.', 1)[1].lower()
        
        # Create backup record
        backup = Backup(
            device_id=device_id,
            user_id=user_id,
            filename=filename,
            file_path=file_path,
            file_size=file_size,
            file_type=file_type,
            checksum=file_hash,
            version=device.version,
            notes=request.form.get('notes'),
            backup_method=request.form.get('backup_method', 'manual')
        )
        
        db.session.add(backup)
        device.last_backup = datetime.utcnow()
        db.session.commit()
        
        return jsonify({
            'message': 'Backup uploaded successfully',
            'backup': backup.to_dict()
        }), 201
    except Exception as e:
        db.session.rollback()
        if os.path.exists(file_path):
            os.remove(file_path)
        return jsonify({'error': str(e)}), 500

@backups_bp.route('/<int:backup_id>/download', methods=['GET'])
@jwt_required()
def download_backup(backup_id):
    """Download backup file"""
    user_id = get_jwt_identity()
    backup = Backup.query.filter_by(id=backup_id, user_id=user_id).first()
    
    if not backup:
        return jsonify({'error': 'Backup not found'}), 404
    
    if not os.path.exists(backup.file_path):
        return jsonify({'error': 'Backup file not found'}), 404
    
    try:
        return send_file(
            backup.file_path,
            as_attachment=True,
            download_name=backup.filename
        )
    except Exception as e:
        return jsonify({'error': str(e)}), 500

@backups_bp.route('/<int:backup_id>', methods=['DELETE'])
@jwt_required()
def delete_backup(backup_id):
    """Delete a backup"""
    user_id = get_jwt_identity()
    backup = Backup.query.filter_by(id=backup_id, user_id=user_id).first()
    
    if not backup:
        return jsonify({'error': 'Backup not found'}), 404
    
    try:
        # Delete file
        if os.path.exists(backup.file_path):
            os.remove(backup.file_path)
        
        # Delete database record
        db.session.delete(backup)
        db.session.commit()
        
        return jsonify({'message': 'Backup deleted successfully'}), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500
