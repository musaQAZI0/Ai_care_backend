using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    [DbContext(typeof(CareDbContext))]
    [Migration("20260819140000_AddConversationMessaging")]
    public partial class AddConversationMessaging : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                create table if not exists conversations (
                    id uuid primary key,
                    service_user_id uuid null,
                    subject text not null,
                    status text not null,
                    created_by_user_id uuid not null,
                    created_at timestamptz not null,
                    updated_at timestamptz not null,
                    organization_id uuid not null,
                    branch_id uuid null
                );
                create index if not exists ix_conversations_tenant_updated
                    on conversations(organization_id, branch_id, updated_at desc);
                create index if not exists ix_conversations_service_user
                    on conversations(organization_id, service_user_id);

                create table if not exists conversation_participants (
                    conversation_id uuid not null references conversations(id) on delete cascade,
                    user_id uuid not null,
                    joined_at timestamptz not null,
                    left_at timestamptz null,
                    last_read_at timestamptz null,
                    primary key(conversation_id, user_id)
                );
                create index if not exists ix_conversation_participants_user
                    on conversation_participants(user_id, left_at);

                create table if not exists conversation_messages (
                    id uuid primary key,
                    conversation_id uuid not null references conversations(id) on delete cascade,
                    sender_user_id uuid not null,
                    body text not null,
                    sent_at timestamptz not null,
                    edited_at timestamptz null,
                    deleted_at timestamptz null,
                    reply_to_message_id uuid null references conversation_messages(id)
                );
                create index if not exists ix_conversation_messages_order
                    on conversation_messages(conversation_id, sent_at);

                create table if not exists conversation_message_attachments (
                    message_id uuid not null references conversation_messages(id) on delete cascade,
                    document_id uuid not null,
                    primary key(message_id, document_id)
                );

                create table if not exists conversation_message_reads (
                    message_id uuid not null references conversation_messages(id) on delete cascade,
                    user_id uuid not null,
                    read_at timestamptz not null,
                    primary key(message_id, user_id)
                );
                create index if not exists ix_conversation_message_reads_user
                    on conversation_message_reads(user_id, read_at desc);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop table if exists conversation_message_reads;
                drop table if exists conversation_message_attachments;
                drop table if exists conversation_messages;
                drop table if exists conversation_participants;
                drop table if exists conversations;
                """);
        }
    }
}
