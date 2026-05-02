from flask import Blueprint, request, jsonify
from flask_jwt_extended import jwt_required, get_jwt_identity
from app import db
from models.script import Script
from models.device import Device

scripts_bp = Blueprint('scripts', __name__)

@scripts_bp.route('', methods=['GET'])
@jwt_required()
def get_scripts():
    """Get all scripts for current user"""
    user_id = get_jwt_identity()
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 20, type=int)
    device_type = request.args.get('device_type')
    
    query = Script.query.filter_by(user_id=user_id)
    
    if device_type:
        query = query.filter_by(device_type=device_type)
    
    scripts = query.order_by(Script.updated_at.desc()).paginate(page=page, per_page=per_page)
    
    return jsonify({
        'data': [script.to_dict() for script in scripts.items],
        'pagination': {
            'page': page,
            'per_page': per_page,
            'total': scripts.total,
            'pages': scripts.pages
        }
    }), 200

@scripts_bp.route('/<int:script_id>', methods=['GET'])
@jwt_required()
def get_script(script_id):
    """Get script by ID"""
    user_id = get_jwt_identity()
    script = Script.query.filter_by(id=script_id, user_id=user_id).first()
    
    if not script:
        return jsonify({'error': 'Script not found'}), 404
    
    return jsonify({
        **script.to_dict(),
        'content': script.content  # Include full content
    }), 200

@scripts_bp.route('', methods=['POST'])
@jwt_required()
def create_script():
    """Create a new script"""
    user_id = get_jwt_identity()
    data = request.get_json()
    
    if not data or not data.get('name') or not data.get('content'):
        return jsonify({'error': 'Missing required fields'}), 400
    
    try:
        script = Script(
            user_id=user_id,
            name=data['name'],
            description=data.get('description'),
            content=data['content'],
            device_type=data.get('device_type', 'Mikrotik'),
            vendor=data.get('vendor', 'Mikrotik'),
            tags=','.join(data['tags']) if data.get('tags') else None
        )
        
        db.session.add(script)
        db.session.commit()
        
        return jsonify({
            'message': 'Script created successfully',
            'script': script.to_dict()
        }), 201
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@scripts_bp.route('/<int:script_id>', methods=['PUT'])
@jwt_required()
def update_script(script_id):
    """Update script"""
    user_id = get_jwt_identity()
    script = Script.query.filter_by(id=script_id, user_id=user_id).first()
    
    if not script:
        return jsonify({'error': 'Script not found'}), 404
    
    data = request.get_json()
    
    try:
        if 'name' in data:
            script.name = data['name']
        if 'description' in data:
            script.description = data['description']
        if 'content' in data:
            script.content = data['content']
            script.version += 1
        if 'is_active' in data:
            script.is_active = data['is_active']
        if 'tags' in data:
            script.tags = ','.join(data['tags']) if data['tags'] else None
        
        db.session.commit()
        
        return jsonify({
            'message': 'Script updated successfully',
            'script': script.to_dict()
        }), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@scripts_bp.route('/<int:script_id>', methods=['DELETE'])
@jwt_required()
def delete_script(script_id):
    """Delete script"""
    user_id = get_jwt_identity()
    script = Script.query.filter_by(id=script_id, user_id=user_id).first()
    
    if not script:
        return jsonify({'error': 'Script not found'}), 404
    
    try:
        db.session.delete(script)
        db.session.commit()
        return jsonify({'message': 'Script deleted successfully'}), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500
