from flask import Blueprint, request, jsonify, send_file
from flask_jwt_extended import jwt_required, get_jwt_identity
from app import db, app
from models.config import Config
from models.device import Device
import os
from datetime import datetime
from werkzeug.utils import secure_filename

configs_bp = Blueprint('configs', __name__)

@configs_bp.route('', methods=['GET'])
@jwt_required()
def get_configs():
    """Get all configs for current user"""
    user_id = get_jwt_identity()
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 20, type=int)
    device_id = request.args.get('device_id', type=int)
    
    query = Config.query.filter(Config.user_id == user_id)
    
    if device_id:
        query = query.filter_by(device_id=device_id)
    
    configs = query.order_by(Config.updated_at.desc()).paginate(page=page, per_page=per_page)
    
    return jsonify({
        'data': [config.to_dict() for config in configs.items],
        'pagination': {
            'page': page,
            'per_page': per_page,
            'total': configs.total,
            'pages': configs.pages
        }
    }), 200

@configs_bp.route('/<int:config_id>', methods=['GET'])
@jwt_required()
def get_config(config_id):
    """Get config by ID"""
    user_id = get_jwt_identity()
    config = Config.query.filter_by(id=config_id, user_id=user_id).first()
    
    if not config:
        return jsonify({'error': 'Config not found'}), 404
    
    return jsonify({
        **config.to_dict(),
        'content': config.content  # Include full content
    }), 200

@configs_bp.route('', methods=['POST'])
@jwt_required()
def create_config():
    """Create a new configuration"""
    user_id = get_jwt_identity()
    data = request.get_json()
    
    if not data or not data.get('device_id') or not data.get('name'):
        return jsonify({'error': 'Missing required fields'}), 400
    
    # Verify device ownership
    device = Device.query.filter_by(id=data['device_id'], user_id=user_id).first()
    if not device:
        return jsonify({'error': 'Device not found or unauthorized'}), 404
    
    try:
        config = Config(
            device_id=data['device_id'],
            user_id=user_id,
            name=data['name'],
            description=data.get('description'),
            content=data.get('content', ''),
            config_type=data.get('config_type', 'rsc'),
            tags=','.join(data['tags']) if data.get('tags') else None
        )
        
        db.session.add(config)
        db.session.commit()
        
        return jsonify({
            'message': 'Config created successfully',
            'config': config.to_dict()
        }), 201
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@configs_bp.route('/<int:config_id>', methods=['PUT'])
@jwt_required()
def update_config(config_id):
    """Update configuration"""
    user_id = get_jwt_identity()
    config = Config.query.filter_by(id=config_id, user_id=user_id).first()
    
    if not config:
        return jsonify({'error': 'Config not found'}), 404
    
    data = request.get_json()
    
    try:
        if 'name' in data:
            config.name = data['name']
        if 'description' in data:
            config.description = data['description']
        if 'content' in data:
            config.content = data['content']
            config.version += 1
        if 'is_active' in data:
            config.is_active = data['is_active']
        if 'tags' in data:
            config.tags = ','.join(data['tags']) if data['tags'] else None
        
        db.session.commit()
        
        return jsonify({
            'message': 'Config updated successfully',
            'config': config.to_dict()
        }), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@configs_bp.route('/<int:config_id>/download', methods=['GET'])
@jwt_required()
def download_config(config_id):
    """Download configuration file"""
    user_id = get_jwt_identity()
    config = Config.query.filter_by(id=config_id, user_id=user_id).first()
    
    if not config:
        return jsonify({'error': 'Config not found'}), 404
    
    try:
        # Create temporary file with config content
        config_dir = app.config['CONFIG_STORAGE_PATH']
        os.makedirs(config_dir, exist_ok=True)
        
        filename = secure_filename(f"{config.name}.{config.config_type}")
        file_path = os.path.join(config_dir, filename)
        
        with open(file_path, 'w') as f:
            f.write(config.content)
        
        return send_file(
            file_path,
            as_attachment=True,
            download_name=filename
        )
    except Exception as e:
        return jsonify({'error': str(e)}), 500

@configs_bp.route('/<int:config_id>', methods=['DELETE'])
@jwt_required()
def delete_config(config_id):
    """Delete configuration"""
    user_id = get_jwt_identity()
    config = Config.query.filter_by(id=config_id, user_id=user_id).first()
    
    if not config:
        return jsonify({'error': 'Config not found'}), 404
    
    try:
        db.session.delete(config)
        db.session.commit()
        return jsonify({'message': 'Config deleted successfully'}), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500
