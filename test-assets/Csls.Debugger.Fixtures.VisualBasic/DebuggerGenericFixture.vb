Imports System.Diagnostics

Namespace Global.Csls.Debugger.Fixtures.VisualBasic
    ''' <summary>
    ''' Provides a closed generic Visual Basic value for debugger construction tests.
    ''' </summary>
    <DebuggerDisplay("generic={Me._value}", Type:="visual-basic-generic")>
    Friend NotInheritable Class DebuggerGenericFixture(Of T)
        Private ReadOnly _value As T

        ''' <summary>
        ''' Initializes the generic Visual Basic debugger value with its default value.
        ''' </summary>
        Friend Sub New()
            _value = Nothing
        End Sub

        ''' <summary>
        ''' Initializes the generic Visual Basic debugger value.
        ''' </summary>
        ''' <param name="value">The value retained by the constructed instance.</param>
        Friend Sub New(value As T)
            _value = value
        End Sub

        ''' <summary>
        ''' Gets the value retained by the constructed instance.
        ''' </summary>
        Friend ReadOnly Property Value As T
            Get
                Return _value
            End Get
        End Property
    End Class
End Namespace
