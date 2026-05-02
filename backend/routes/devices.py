from flask import Blueprint, request, jsonify
from flask_jwt_extended import jwt_required, get_jwt_identity
from app import db
from models.device import Device
from models.user import User

devices_bp = Blueprint('devices', __name__)

@devices_bp.route('', methods=['GET'])
@jwt_required()
def get_devices():
    """Get all devices for current user"""
    user_id = get_jwt_identity()
    page = request.args.get('page', 1, type=int)
    per_page = request.args.get('per_page', 20, type=int)
    
    query = Device.query.filter_by(user_id=user_id)
    
    # Filters
    if request.args.get('device_type'):
        query = query.filter_by(device_type=request.args.get('device_type'))
    if request.args.get('vendor'):
        query = query.filter_by(vendor=request.args.get('vendor'))
    if request.args.get('is_active'):
        is_active = request.args.get('is_active').lower() == 'true'
        query = query.filter_by(is_active=is_active)
    
    devices = query.paginate(page=page, per_page=per_page)
    
    return jsonify({
        'data': [device.to_dict() for device in devices.items],
        'pagination': {
            'page': page,
            'per_page': per_page,
            'total': devices.total,
            'pages': devices.pages
        }
    }), 200

@devices_bp.route('/<int:device_id>', methods=['GET'])
@jwt_required()
def get_device(device_id):
    """Get device by ID"""
    user_id = get_jwt_identity()
    device = Device.query.filter_by(id=device_id, user_id=user_id).first()
    
    if not device:
        return jsonify({'error': 'Device not found'}), 404
    
    return jsonify(device.to_dict()), 200

@devices_bp.route('', methods=['POST'])
@jwt_required()
def create_device():
    """Create a new device"""
    user_id = get_jwt_identity()
    data = request.get_json()
    
    if not data or not data.get('name') or not data.get('ip_address'):
        return jsonify({'error': 'Missing required fields'}), 400
    
    try:
        device = Device(
            user_id=user_id,
            name=data['name'],
            ip_address=data['ip_address'],
            serial=data.get('serial'),
            device_type=data.get('device_type', 'Mikrotik'),
            vendor=data.get('vendor', 'Mikrotik'),
            version=data.get('version'),
            board=data.get('board'),
            port=data.get('port', 22),
            protocol=data.get('protocol', 'ssh'),
            notes=data.get('notes')
        )
        
        # Handle password if provided
        if data.get('password'):
            device.password_hash = data['password']  # TODO: Encrypt password
        
        db.session.add(device)
        db.session.commit()
        
        return jsonify({
            'message': 'Device created successfully',
            'device': device.to_dict()
        }), 201
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@devices_bp.route('/<int:device_id>', methods=['PUT'])
@jwt_required()
def update_device(device_id):
    """Update device"""
    user_id = get_jwt_identity()
    device = Device.query.filter_by(id=device_id, user_id=user_id).first()
    
    if not device:
        return jsonify({'error': 'Device not found'}), 404
    
    data = request.get_json()
    
    try:
        # Update allowed fields
        if 'name' in data:
            device.name = data['name']
        if 'ip_address' in data:
            device.ip_address = data['ip_address']
        if 'serial' in data:
            device.serial = data['serial']
        if 'version' in data:
            device.version = data['version']
        if 'board' in data:
            device.board = data['board']
        if 'port' in data:
            device.port = data['port']
        if 'protocol' in data:
            device.protocol = data['protocol']
        if 'notes' in data:
            device.notes = data['notes']
        if 'is_active' in data:
            device.is_active = data['is_active']
        if 'password' in data:
            device.password_hash = data['password']  # TODO: Encrypt password
        
        db.session.commit()
        
        return jsonify({
            'message': 'Device updated successfully',
            'device': device.to_dict()
        }), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500

@devices_bp.route('/<int:device_id>', methods=['DELETE'])
@jwt_required()
def delete_device(device_id):
    """Delete device"""
    user_id = get_jwt_identity()
    device = Device.query.filter_by(id=device_id, user_id=user_id).first()
    
    if not device:
        return jsonify({'error': 'Device not found'}), 404
    
    try:
        db.session.delete(device)
        db.session.commit()
        return jsonify({'message': 'Device deleted successfully'}), 200
    except Exception as e:
        db.session.rollback()
        return jsonify({'error': str(e)}), 500
